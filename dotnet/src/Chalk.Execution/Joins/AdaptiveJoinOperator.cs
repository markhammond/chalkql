using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// A cross-source join whose strategy is chosen from measurement rather than from an estimate (D97,
/// <c>docs/design/20-m5-federation.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// The plan carries both branches and a threshold. At execution the small side is read once into the
/// arena, its distinct non-NULL join keys are counted, and the branch is chosen: the lookup branch
/// when the keys fit in <c>max_keys</c> — which is <c>max_in_list × lookup_max_calls</c>, the number
/// of calls the policy is willing to make — and the local branch otherwise. Both branches then read
/// the small side through a <c>MaterialisedInput</c>, so the rows the decision was taken over are
/// exactly the rows the join runs on.
/// </para>
/// <para>
/// This is the default strategy for a reason: the estimate that would otherwise decide is, for most
/// cross-source joins, a guess. A count of the rows in hand is not.
/// </para>
/// <para>
/// The decision is recorded in <c>Stats.AdaptiveDecisions</c> and nowhere else. It is not in the
/// plan and not in the digest: which branch runs is a property of the data, and a digest that moved
/// with it would stop meaning "the same plan".
/// </para>
/// </remarks>
internal sealed class AdaptiveJoinOperator : OperatorBase
{
    private readonly IBatchOperator _small;
    private readonly Materialisation _materialisation;
    private readonly IBatchOperator _lookupBranch;
    private readonly IBatchOperator _localBranch;
    private readonly int _keyColumn;
    private readonly ChalkType _keyType;
    private readonly long _maxKeys;
    private readonly ColumnarBatch _output;
    private readonly ChalkType[] _smallTypes;

    /// <summary>
    /// The plan's estimate of the small side's rows — the side this join materialises whole for both
    /// branches to replay — or zero where the plan has none (D258.3).
    /// </summary>
    private readonly double _estimatedSmallRows;

    public AdaptiveJoinOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator small,
        Materialisation materialisation,
        IBatchOperator lookupBranch,
        IBatchOperator localBranch,
        int keyColumn,
        ChalkType keyType,
        long maxKeys,
        IReadOnlyList<ChalkType>? smallTypes = null,
        double estimatedSmallRows = 0)
        : base(context, schema, columnTypes, path)
    {
        _smallTypes = smallTypes is null ? [] : [.. smallTypes];
        _estimatedSmallRows = estimatedSmallRows;
        _small = small;
        _materialisation = materialisation;
        _lookupBranch = lookupBranch;
        _localBranch = localBranch;
        _keyColumn = keyColumn;
        _keyType = keyType;
        _maxKeys = maxKeys;
        _output = NewOutput();
    }

    /// <remarks>
    /// Both branches are built with the tree and only one is enumerated. That is not waste: an
    /// operator's plan-shaped scratch registers itself with the ambient scope as it is constructed
    /// (D64), and a branch built at execution time would hold copiers no arena ever acquired.
    /// </remarks>
    protected override async ValueTask DisposeCoreAsync()
    {
        await _small.DisposeAsync().ConfigureAwait(false);
        await _lookupBranch.DisposeAsync().ConfigureAwait(false);
        await _localBranch.DisposeAsync().ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rows = _materialisation.Rows;

        // The small side is read whole and replayed, so its copiers start at the plan's estimate for
        // it rather than doubling a batch at a time (D258.3).
        rows.Begin(PlanCapacity.Rows(Context, _estimatedSmallRows, _smallTypes));

        // One read of the small side, into the arena. Everything after this — the count, both
        // branches — works from these rows and never asks the source again.
        var distinct = new HashSet<object>();
        var buffered = 0;
        await foreach (var batch in _small.ExecuteAsync(ct).ConfigureAwait(false))
        {
            for (var row = 0; row < batch.Count; row++)
            {
                var lane = batch.RowAt(row);
                var key = ScalarValue.FromLane(batch.Column(_keyColumn), lane, _keyType);
                if (!key.IsNull && key.ToHost() is { } identity)
                {
                    distinct.Add(identity);
                }

                rows.AppendRow(batch, row);
                buffered++;
            }
        }

        rows.Finish();

        var lookup = distinct.Count <= _maxKeys;
        Context.Stats.RecordAdaptiveDecision(new AdaptiveDecision
        {
            OperatorPath = Path,
            Branch = lookup ? AdaptiveBranch.Lookup : AdaptiveBranch.Local,
            DistinctKeys = distinct.Count,
            MaxKeys = _maxKeys,
            SmallRows = buffered,
        });

        var branch = lookup ? _lookupBranch : _localBranch;
        try
        {
            await foreach (var batch in branch.ExecuteAsync(ct).ConfigureAwait(false))
            {
                _output.Adopt(batch);
                yield return _output;
            }
        }
        finally
        {
            rows.Release();
        }
    }
}
