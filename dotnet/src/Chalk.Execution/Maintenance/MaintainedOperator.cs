using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A stateful operator of a maintained plan (D343, design 68 §2): in each update it reads all its
/// inputs' changes, brings its state up to date, and then gives its own changes, as batches whose last
/// column is the weight.
/// </summary>
internal abstract class MaintainedOperator : OperatorBase
{
    protected MaintainedOperator(OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types)
        : base(context, schema, types, path)
    {
    }

    /// <summary>Computes the update's changes, which the run then gives batch by batch.</summary>
    protected abstract ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct);

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var output = await ComputeAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < output.BatchCount; i++)
        {
            yield return output.Batch(i);
        }
    }

    /// <summary>Reads every change an input gives in this update, added up.</summary>
    protected static async ValueTask DrainAsync(IBatchOperator input, ChangeSet into, CancellationToken ct)
    {
        into.Clear();
        await foreach (var batch in input.ExecuteAsync(ct).ConfigureAwait(false))
        {
            into.Add(batch);
        }

        into.Seal();
    }

    /// <summary>
    /// Which rows of <paramref name="batch"/> a condition holds for: TRUE only, as a filter keeps
    /// (<c>02-ir.md</c> §4). Written into <paramref name="pass"/>, one entry per row.
    /// </summary>
    protected static void Evaluate(IVectorExpr condition, EvalContext eval, ColumnarBatch batch, Span<bool> pass)
    {
        eval.SetBatch(batch, batch.RowCount);
        var mask = condition.Evaluate(eval);
        if (mask.IsScalar)
        {
            pass[..batch.RowCount].Fill(!mask.Scalar.IsNull && mask.Scalar.Integer != 0);
            return;
        }

        var lanes = Lanes<byte>.From(mask);
        for (var row = 0; row < batch.RowCount; row++)
        {
            pass[row] = lanes.IsValid(row) && lanes[row] != 0;
        }
    }
}

/// <summary>
/// A subtree that reads no table (design 68 §6): constants, and perhaps the clock. Computed in full at
/// the first update and whenever the clock moves, if it reads the clock, and compared with what it
/// gave before; in every other update it gives nothing.
/// </summary>
internal sealed class IslandState : IMaintainedState
{
    public IslandState(OperatorFactory body, IReadOnlyList<ChalkType> types, bool readsClock, int batchSize)
    {
        Body = body;
        ReadsClock = readsClock;
        Types = types;
        Given = new ChangeSet(types);
        Now = new ChangeSet(types);
        Output = new ChangeWriter(types, batchSize);
        Identity = KeyColumns.All(types);
    }

    public OperatorFactory Body { get; }

    public bool ReadsClock { get; }

    public IReadOnlyList<ChalkType> Types { get; }

    /// <summary>What the subtree gave at its last computation, added up.</summary>
    public ChangeSet Given { get; private set; }

    /// <summary>Scratch for this computation's rows.</summary>
    public ChangeSet Now { get; private set; }

    public ChangeWriter Output { get; }

    public KeyColumns Identity { get; }

    public KeyEncoder Encoder { get; } = new();

    /// <summary>The clock at the last computation, or null before the first.</summary>
    public DateTimeOffset? ComputedAt { get; set; }

    public long Bytes => Given.Bytes + Now.Bytes + Output.Bytes;

    /// <summary>Makes this computation's rows the ones given.</summary>
    public void Swap() => (Given, Now) = (Now, Given);

    public void Discard()
    {
        Given.Clear();
        Given.Seal();
        Now.Clear();
        Output.Clear();
        ComputedAt = null;
    }

    /// <summary>What the subtree gave is held; the rows of a computation and the changes are not.</summary>
    public void Trim()
    {
        Now.Trim();
        Output.Trim();
    }
}

internal sealed class IslandChanges : MaintainedOperator
{
    private readonly IslandState _state;
    private readonly IBatchOperator _body;

    public IslandChanges(
        OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types, IslandState state)
        : base(context, path, schema, types)
    {
        _state = state;
        _body = state.Body(context);
    }

    protected override ValueTask DisposeCoreAsync() => _body.DisposeAsync();

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var output = _state.Output;
        output.Clear();
        if (_state.ComputedAt is { } at && (!_state.ReadsClock || at == Context.Now))
        {
            return output;
        }

        // The rows now, added up as a change set of weight one each.
        var now = _state.Now;
        now.Clear();
        await foreach (var batch in _body.ExecuteAsync(ct).ConfigureAwait(false))
        {
            now.AddRows(batch, +1);
        }

        now.Seal();
        Diff(_state.Given, now, output, _state.Identity, _state.Encoder);
        _state.Swap();
        _state.ComputedAt = Context.Now;
        return output;
    }

    /// <summary>Writes what turns <paramref name="before"/> into <paramref name="after"/>: rows out, rows in.</summary>
    internal static void Diff(ChangeSet before, ChangeSet after, ChangeWriter output, KeyColumns identity, KeyEncoder encoder)
    {
        var old = new Dictionary<byte[], int>(ByteKeys.Instance);
        var lookup = old.GetAlternateLookup<ReadOnlySpan<byte>>();
        for (var row = 0; row < before.Count; row++)
        {
            if (before.Weight(row) != 0)
            {
                identity.Encode(encoder, before.Columns, row, normalise: false);
                lookup[encoder.Span] = row;
            }
        }

        for (var row = 0; row < after.Count; row++)
        {
            var weight = after.Weight(row);
            identity.Encode(encoder, after.Columns, row, normalise: false);
            if (lookup.Remove(encoder.Span, out _, out var was))
            {
                weight -= before.Weight(was);
            }

            if (weight != 0)
            {
                output.Write(after.Columns, row, weight);
            }
        }

        foreach (var (_, row) in old)
        {
            output.Write(before.Columns, row, -before.Weight(row));
        }
    }
}

/// <summary>
/// A subtree the plan uses more than once (<c>WITH</c> computed once, D338): its changes computed once
/// in each update, and given to every use.
/// </summary>
internal sealed class SharedState : IMaintainedState
{
    private OperatorContext? _context;
    private IBatchOperator? _body;

    public SharedState(OperatorFactory body, IReadOnlyList<ChalkType> types, int batchSize)
    {
        Factory = body;
        Changes = new ChangeWriter(types, batchSize);
    }

    public OperatorFactory Factory { get; }

    public ChangeWriter Changes { get; }

    /// <summary>The update the changes were computed for, as its context's generation.</summary>
    public int ComputedFor { get; set; } = -1;

    public long Bytes => Changes.Bytes;

    /// <summary>Whether a use in <paramref name="context"/>'s tree has built the body already.</summary>
    public bool IsBuiltIn(OperatorContext context) => ReferenceEquals(context, _context);

    /// <summary>The body's operator in <paramref name="context"/>'s tree, built by the first use there.</summary>
    public IBatchOperator Body(OperatorContext context)
    {
        if (!ReferenceEquals(context, _context))
        {
            _context = context;
            _body = Factory(context);
        }

        return _body!;
    }

    public void Discard()
    {
        Changes.Clear();
        ComputedFor = -1;
    }

    public void Trim() => Changes.Trim();
}

internal sealed class SharedChanges : MaintainedOperator
{
    private readonly SharedState _state;
    private readonly IBatchOperator _body;
    private readonly bool _owner;

    public SharedChanges(
        OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types, SharedState state)
        : base(context, path, schema, types)
    {
        _state = state;
        _owner = !state.IsBuiltIn(context);
        _body = state.Body(context);
    }

    protected override ValueTask DisposeCoreAsync() => _owner ? _body.DisposeAsync() : default;

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        if (_state.ComputedFor != Context.Generation)
        {
            var changes = _state.Changes;
            changes.Clear();
            await foreach (var batch in _body.ExecuteAsync(ct).ConfigureAwait(false))
            {
                var columns = batch.Columns;
                for (var i = 0; i < batch.Count; i++)
                {
                    var row = batch.RowAt(i);
                    changes.Write(columns, row, Weights.Of(batch, row));
                }
            }

            _state.ComputedFor = Context.Generation;
        }

        return _state.Changes;
    }
}

