using Apache.Arrow.Memory;
using Chalk.Sources;
using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// A compiled expression. The IR <c>Expr</c> is turned into a tree of these once per plan, and each
/// evaluation processes a whole batch (D11: a vectorised interpreter, not
/// <c>System.Linq.Expressions</c>).
/// </summary>
/// <remarks>
/// A node owns reusable scratch, so a tree belongs to one execution and one thread. Future backends —
/// IL emit, or codegen over arrays — plug in here; nothing in an operator depends on how expressions
/// run (§6.4).
/// </remarks>
internal interface IVectorExpr
{
    /// <summary>The result type, straight from the IR: executors never infer or coerce (§5).</summary>
    ChalkType Type { get; }

    Vector Evaluate(EvalContext context);
}

/// <summary>What an expression is evaluated against: the batch, the bound parameters and the clock.</summary>
/// <remarks>
/// A selected batch (§2, D62) is evaluated over <em>every</em> lane, because kernels have no selected
/// variant — but a lane the filter removed may hold a value the query took care to exclude, and the
/// guarded kernels (division, checked arithmetic, a cast that may fail) would raise on it. So the
/// context carries a bitmap of the selected lanes, and the validity helpers of
/// <see cref="VectorExprBase"/> intersect it into every result: an unselected lane is invalid, every
/// guard already skips invalid lanes, and nothing downstream ever reads one.
/// </remarks>
internal sealed class EvalContext : Vectors.IArenaScratch
{
    private readonly Operators.OperatorContext _execution;
    private Vectors.ArenaBuffer _mask = new();
    private bool _selected;

    public EvalContext(Operators.OperatorContext execution)
    {
        _execution = execution;
        Vectors.ScratchScope.Register(this);
    }

    /// <summary>
    /// The lanes this batch selected, bit per row, or empty when every lane is selected — which is
    /// the common case and costs nothing.
    /// </summary>
    public ReadOnlySpan<byte> SelectionMask =>
        _selected ? _mask.Bytes.AsSpan(0, Vectors.Validity.ByteCount(Length)) : default;

    public void Acquire(ExecutionArena arena) => _mask.Acquire(arena);

    public void Release()
    {
        _mask.Release();
        _selected = false;
    }

    /// <summary>The batch under evaluation. Null only for a plan with no input rows to read.</summary>
    public ColumnarBatch? Batch { get; private set; }

    /// <summary>Rows in <see cref="Batch"/>. Set even when there is no batch (a constant projection).</summary>
    public int Length { get; private set; }

    /// <summary>Bound parameter values, by <c>DynamicParam.index</c>.</summary>
    public IReadOnlyList<ScalarValue> Parameters => _execution.Parameters;

    /// <summary>Where owned buffers come from when an expression has to build one.</summary>
    public MemoryAllocator Allocator => _execution.Allocator;

    /// <summary>This execution's arena, for a Tier 2 kernel that needs scratch of its own (D79).</summary>
    public ExecutionArena Arena => _execution.Arena;

    /// <summary>Evaluated once per execution, so two rows never disagree about "now" (§6).</summary>
    public DateTimeOffset Now => _execution.Now;

    /// <summary>Changes with every execution, so a cache built from the parameters knows to rebuild.</summary>
    public int Generation => _execution.Generation;

    /// <summary>
    /// Changes with every batch this context is pointed at, so a node shared by several expressions
    /// of one operator knows whether it has already answered for this one (D293). It starts at zero,
    /// which is also the answer for evaluation that never points the context at a batch at all.
    /// </summary>
    public long BatchSequence { get; private set; }

    /// <summary>Points the context at the next batch. Cheap: an operator does this per batch.</summary>
    public void SetBatch(ColumnarBatch? batch, int length)
    {
        BatchSequence++;
        Batch = batch;
        Length = length;
        _selected = batch is { HasSelection: true };
        if (!_selected)
        {
            return;
        }

        var bytes = Vectors.Validity.ByteCount(length);
        _mask.Ensure(Math.Max(bytes, 1));
        var bits = _mask.Bytes.AsSpan(0, bytes);
        bits.Clear();
        foreach (var row in batch!.Selection)
        {
            Apache.Arrow.BitUtility.SetBit(bits, row);
        }
    }
}
