using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Windowing;

/// <summary>One key of a window's ordering, resolved to a column and a direction.</summary>
internal readonly record struct WindowOrderKey(int Column, bool Descending, bool NullsFirst);

/// <summary>
/// A frame offset: the literal the planner wrote down, or the parameter slot to read at execution
/// (<c>13-window-functions.md</c> §2). Bound once per execution, never per row.
/// </summary>
internal sealed class WindowConstant
{
    private readonly ScalarValue? _constant;
    private readonly int _parameter;

    private WindowConstant(ScalarValue? constant, int parameter)
    {
        _constant = constant;
        _parameter = parameter;
    }

    /// <summary>Reads a literal or a dynamic parameter; anything else is a planner bug.</summary>
    public static WindowConstant Of(Expr expr, string path) => expr.KindCase switch
    {
        Expr.KindOneofCase.Literal => new WindowConstant(Literals.ToScalar(expr), -1),
        Expr.KindOneofCase.Param => new WindowConstant(null, (int)expr.Param.Index),
        _ => throw new InvalidPlanException(
            "I-IR-11",
            path,
            $"a window constant is a literal or a dynamic parameter; found {expr.KindCase}"),
    };

    /// <summary>
    /// The literal the planner wrote down, or null for a parameter this execution has yet to bind.
    /// The streaming path of D258.4 asks because a <c>LAG</c> offset only decides at compilation
    /// whether the call looks backwards or forwards, and a path is chosen at compilation.
    /// </summary>
    public ScalarValue? Literal => _constant;

    public ScalarValue Bind(IReadOnlyList<ScalarValue> parameters)
    {
        if (_constant is not null)
        {
            return _constant;
        }

        if (_parameter >= parameters.Count)
        {
            throw new InvalidPlanException(
                "I-IR-11",
                "Window",
                $"a window constant reads parameter {_parameter} but only {parameters.Count} were bound");
        }

        return parameters[_parameter];
    }
}

/// <summary>One frame bound: its kind and, for PRECEDING and FOLLOWING, its offset.</summary>
internal sealed class WindowBound
{
    public required FrameBoundKind Kind { get; init; }

    public WindowConstant? Offset { get; init; }

    public bool HasOffset => Kind is FrameBoundKind.Preceding or FrameBoundKind.Following;
}

/// <summary>
/// Everything one <c>Window</c> node fixes at compilation: the partitioning, the ordering, the frame
/// and the calls. Nothing here depends on the data, so it is shared by every execution of the plan.
/// </summary>
internal sealed class WindowSpec
{
    public required int[] PartitionKeys { get; init; }

    public required WindowOrderKey[] Order { get; init; }

    public required FrameMode Mode { get; init; }

    public required WindowBound Lower { get; init; }

    public required WindowBound Upper { get; init; }

    public required FrameExclusion Exclusion { get; init; }

    /// <summary>The input row's logical types, in order.</summary>
    public required ChalkType[] InputTypes { get; init; }

    /// <summary>
    /// True when the frame's last row is the current row or one before it, which is the shape the
    /// streaming path of D258.4 needs: nothing after the cursor is ever in a frame, so a row's answer
    /// is final once its own peer group is (13-window-functions.md §4.1).
    /// </summary>
    public bool EndsAtOrBeforeCurrentRow =>
        Upper.Kind is FrameBoundKind.CurrentRow or FrameBoundKind.Preceding;

    /// <summary>
    /// True when the frame's first row is a bounded distance behind the current row, so the rows a
    /// call reads behind the cursor are the frame's own and not the partition's.
    /// </summary>
    public bool StartsWithinBoundedDistance =>
        Lower.Kind is FrameBoundKind.Preceding or FrameBoundKind.CurrentRow;

    /// <summary>True when either bound carries an offset — the <c>RANGE n PRECEDING</c> shapes.</summary>
    public bool HasOffsets => Lower.HasOffset || Upper.HasOffset;

    /// <summary>
    /// True when the frame grows from the partition's first row to the current one, so no row behind
    /// the cursor is ever read twice and a running accumulator needs nothing in memory.
    /// </summary>
    public bool RunningToCurrentRow =>
        Lower.Kind == FrameBoundKind.UnboundedPreceding && Upper.Kind == FrameBoundKind.CurrentRow;

    /// <summary>True when a call has to look at the frame at all — false for a whole-partition frame.</summary>
    public bool WholePartition =>
        Lower.Kind == FrameBoundKind.UnboundedPreceding
        && Upper.Kind == FrameBoundKind.UnboundedFollowing
        && Exclusion == FrameExclusion.NoOthers;

    /// <summary>The single order key a RANGE frame with an offset compares against.</summary>
    public WindowOrderKey RangeKey => Order[0];

    /// <summary>The layout of the column an order key reads.</summary>
    public ColumnKind KindOf(int column) => ColumnKinds.Of(InputTypes[column]);
}
