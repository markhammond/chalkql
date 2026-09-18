using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// One window call's evaluation over the buffered input (§4). An evaluator computes a whole partition
/// at a time and then answers row by row when the operator emits.
/// </summary>
/// <remarks>
/// Everything that scales with the data is rented from the execution's arena between
/// <see cref="Begin"/> and <see cref="Release"/>, exactly as the aggregate's state is (ADR 0012), so
/// a window query leaves the arena empty and <c>MaxBytes</c> catches a partition that is too large.
/// </remarks>
internal abstract class WindowCallEvaluator
{
    protected WindowCallEvaluator(ChalkType resultType)
    {
        ResultType = resultType;
        ResultKind = ColumnKinds.Of(resultType);
        ResultWidth = ColumnKinds.Width(ResultKind);
    }

    protected ChalkType ResultType { get; }

    protected ColumnKind ResultKind { get; }

    protected int ResultWidth { get; }

    /// <summary>Claims the storage this call needs for a run of <paramref name="rows"/> rows.</summary>
    public abstract void Begin(ExecutionArena arena, int rows);

    /// <summary>Hands everything back, in the run's <c>finally</c>.</summary>
    public abstract void Release(ExecutionArena arena);

    /// <summary>Computes the results for one partition, the rows <c>[start, end)</c>.</summary>
    public abstract void Compute(WindowRun run, int start, int end);

    /// <summary>Appends one row's result to the output column.</summary>
    public abstract void Emit(ColumnCopier copier, WindowRun run, int row);
}

/// <summary>
/// A call whose answer is <em>a row of the input</em> — the navigation functions, the frame's first,
/// last and n-th value, and MIN and MAX. Storing the row index rather than the value means every
/// type works, including the variable-length ones, with no copying until the output is built.
/// </summary>
/// <remarks>
/// Two indexes are special: <c>-1</c> is NULL and <c>-2</c> is the call's default value, which is
/// what <c>LAG(x, n, d)</c> produces when it walks off the start of the partition.
/// </remarks>
internal abstract class WindowRowEvaluator : WindowCallEvaluator
{
    protected const int NullRow = -1;
    protected const int DefaultRow = -2;

    private readonly int _valueColumn;
    private readonly WindowConstant? _default;

    protected WindowRowEvaluator(ChalkType resultType, int valueColumn, WindowConstant? defaultValue)
        : base(resultType)
    {
        _valueColumn = valueColumn;
        _default = defaultValue;
    }

    protected int[] Source { get; private set; } = [];

    protected int ValueColumn => _valueColumn;

    public override void Begin(ExecutionArena arena, int rows) => Source = arena.Rent<int>(rows);

    public override void Release(ExecutionArena arena)
    {
        arena.Return(Source);
        Source = [];
    }

    public override void Emit(ColumnCopier copier, WindowRun run, int row)
    {
        var source = Source[row];
        if (source == DefaultRow && _default is not null)
        {
            copier.AppendConstant(_default.Bind(run.Parameters), 1);
            return;
        }

        if (source < 0)
        {
            copier.AppendRaw(default, valid: false);
            return;
        }

        copier.AppendRow(run.Columns[_valueColumn].View, source);
    }
}

/// <summary>
/// A call whose answer is a <em>computed</em> value: the ranking family and the frame aggregates.
/// The results are kept as raw lanes of the result type, which is what the output copier appends.
/// </summary>
internal abstract class WindowValueEvaluator : WindowCallEvaluator
{
    protected WindowValueEvaluator(ChalkType resultType)
        : base(resultType)
    {
    }

    protected byte[] Values { get; private set; } = [];

    protected bool[] Valid { get; private set; } = [];

    public override void Begin(ExecutionArena arena, int rows)
    {
        Values = arena.Rent<byte>(Math.Max(rows, 1) * ResultWidth);
        Valid = arena.Rent<bool>(Math.Max(rows, 1));
    }

    public override void Release(ExecutionArena arena)
    {
        arena.Return(Values);
        arena.Return(Valid);
        Values = [];
        Valid = [];
    }

    public override void Emit(ColumnCopier copier, WindowRun run, int row) =>
        copier.AppendRaw(Values.AsSpan(row * ResultWidth, ResultWidth), Valid[row]);

    /// <summary>The lane row <paramref name="row"/>'s result is written into.</summary>
    protected Span<byte> Lane(int row) => Values.AsSpan(row * ResultWidth, ResultWidth);
}
