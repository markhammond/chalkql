using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// A <c>ContextTable</c>: the relation the host bound under this name, materialised into batches at
/// execution start (<c>docs/design/16-entitlements.md</c> §2 and §4).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as a <c>VirtualTable</c> and deliberately so — a context table <em>is</em> literal
/// rows, and the only difference is where they come from. A list small enough to fold becomes
/// literals in the plan; one above <c>FoldMaxRows</c> stays a relation, so its rows are never in the
/// plan text, never in the digest and never in a plan cache key, and reach the engine only here. A
/// host that binds a hundred thousand tenancy identifiers therefore plans one statement rather than
/// a hundred thousand literals, and the identifiers stay out of every artefact a plan leaves behind.
/// </para>
/// <para>
/// The rows are bound against the node's own declared row type once per execution — not per batch —
/// so a value the type cannot hold is an error before any row is produced, naming the relation and
/// the column.
/// </para>
/// </remarks>
internal sealed class ContextTableOperator : OperatorBase
{
    private readonly string _name;
    private readonly ChalkType[] _columnTypes;
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnarBatch _output;

    public ContextTableOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        string name)
        : base(context, schema, columnTypes, path)
    {
        _name = name;
        _columnTypes = [.. columnTypes];
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _output = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var rows = Bind();
        var batchSize = Context.Settings.BatchSize;
        for (var start = 0; start < rows.Length; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, rows.Length - start);
            _output.Begin(count);
            for (var c = 0; c < _copiers.Length; c++)
            {
                _copiers[c].Begin();
                for (var r = 0; r < count; r++)
                {
                    _copiers[c].AppendConstant(rows[start + r][c], 1);
                }

                _output.Set(c, _copiers[c].FinishView());
            }

            yield return _output;
        }
    }

    /// <summary>This execution's rows for this name, bound to the node's declared column types.</summary>
    private ScalarValue[][] Bind()
    {
        var supplied = Context.ContextRelation(_name)
            ?? throw new ExecutionException(
                Context.PlanDigest,
                Path,
                $"the plan reads the context relation '{_name}' and the execution bound none. "
                + "A list too large to fold stays a relation the executor materialises, so it has "
                + "to be bound at execution as well as at prepare.");

        var bound = new ScalarValue[supplied.Count][];
        for (var r = 0; r < supplied.Count; r++)
        {
            var row = supplied[r];
            if (row.Count != _columnTypes.Length)
            {
                throw new ExecutionException(
                    Context.PlanDigest,
                    Path,
                    $"row {r} of the context relation '{_name}' has {row.Count} value(s) and the "
                    + $"plan declares {_columnTypes.Length} column(s).");
            }

            try
            {
                bound[r] = ParameterBinder.Bind(row, _columnTypes);
            }
            catch (ArgumentException failure)
            {
                throw new ExecutionException(
                    Context.PlanDigest,
                    Path,
                    $"row {r} of the context relation '{_name}' does not match the column types "
                    + $"the plan declares: {failure.Message}");
            }
        }

        return bound;
    }
}
