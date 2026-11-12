using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Kleene <c>AND</c> and <c>OR</c> (<c>02-ir.md</c> §5), n-ary. Each operand moves the lane towards its
/// absorbing value — FALSE for AND, TRUE for OR — and a lane that never reaches it but saw a NULL is
/// NULL. Each operand after the first is evaluated over the lanes the ones before it left undecided,
/// and once every lane has been absorbed the rest are not evaluated at all: the lane-mask short
/// circuit §6.4 asks for. So <c>x &lt;&gt; 0 AND y / x &gt; 1</c> divides by no zero, and a rule's
/// condition after the test of its role is evaluated only for the rows whose principal holds it.
/// </summary>
internal sealed class KleeneExpr : VectorExprBase
{
    private readonly IVectorExpr[] _operands;
    private readonly bool _isAnd;
    private byte[] _sawNull = [];
    private readonly LaneScope _undecided = new();

    public KleeneExpr(ChalkType type, bool isAnd, IVectorExpr[] operands)
        : base(type)
    {
        _isAnd = isAnd;
        _operands = operands;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        if (_sawNull.Length < length)
        {
            _sawNull = new byte[length];
        }

        var result = Scratch.Values<byte>(length);
        var sawNull = _sawNull.AsSpan(0, length);
        var absorbing = (byte)(_isAnd ? 0 : 1);
        result.Fill((byte)(_isAnd ? 1 : 0));
        sawNull.Clear();

        var absorbed = 0;
        for (var k = 0; k < _operands.Length; k++)
        {
            var vector = k == 0
                ? _operands[k].Evaluate(context)
                : _undecided.Evaluate(context, _operands[k], result, absorbing);
            var lanes = Lanes<byte>.From(vector);
            for (var i = 0; i < length; i++)
            {
                if (result[i] == absorbing)
                {
                    continue;
                }

                if (!lanes.IsValid(i))
                {
                    sawNull[i] = 1;
                }
                else if ((lanes[i] != 0) == !_isAnd)
                {
                    result[i] = absorbing;
                    absorbed++;
                }
            }

            if (absorbed == length)
            {
                break;
            }
        }

        var nulls = 0;
        var bits = Scratch.BeginValidity(length);
        for (var i = 0; i < length; i++)
        {
            if (result[i] == absorbing || sawNull[i] == 0)
            {
                BitUtility.SetBit(bits, i);
            }
            else
            {
                nulls++;
            }
        }

        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// An operand evaluated over the selected lanes still undecided — those whose answer so far is not
/// <c>decided</c> — narrowed as a <c>CASE</c> branch is (F289), or over the batch as it is when every
/// selected lane is undecided. The lanes are owned here, a bit per row, grown and never shrunk.
/// </summary>
internal sealed class LaneScope
{
    private byte[] _lanes = [];

    public Vector Evaluate(EvalContext context, IVectorExpr operand, ReadOnlySpan<byte> answers, byte decided)
    {
        var lanes = Begin(context.Length);
        var selection = context.SelectionMask;
        var open = 0;
        var selected = 0;
        for (var i = 0; i < context.Length; i++)
        {
            if (!selection.IsEmpty && !BitUtility.GetBit(selection, i))
            {
                continue;
            }

            selected++;
            if (answers[i] != decided)
            {
                BitUtility.SetBit(lanes, i);
                open++;
            }
        }

        return Within(context, operand, open == selected);
    }

    /// <summary>The same, the undecided lanes being those whose choice is still <paramref name="none"/>.</summary>
    public Vector Evaluate(EvalContext context, IVectorExpr operand, ReadOnlySpan<int> choice, int none)
    {
        var lanes = Begin(context.Length);
        var selection = context.SelectionMask;
        var open = 0;
        var selected = 0;
        for (var i = 0; i < context.Length; i++)
        {
            if (!selection.IsEmpty && !BitUtility.GetBit(selection, i))
            {
                continue;
            }

            selected++;
            if (choice[i] == none)
            {
                BitUtility.SetBit(lanes, i);
                open++;
            }
        }

        return Within(context, operand, open == selected);
    }

    private Span<byte> Begin(int length)
    {
        var bytes = Validity.ByteCount(length);
        if (_lanes.Length < Math.Max(bytes, 1))
        {
            _lanes = new byte[Math.Max(bytes, 1)];
        }

        var lanes = _lanes.AsSpan(0, bytes);
        lanes.Clear();
        return lanes;
    }

    private Vector Within(EvalContext context, IVectorExpr operand, bool everyLane)
    {
        if (everyLane)
        {
            return operand.Evaluate(context);
        }

        var scope = context.Narrow(_lanes);
        try
        {
            return operand.Evaluate(context);
        }
        finally
        {
            context.Restore(scope);
        }
    }
}

/// <summary>NOT: NULL stays NULL, TRUE and FALSE swap.</summary>
internal sealed class NotExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;

    public NotExpr(ChalkType type, IVectorExpr operand)
        : base(type) => _operand = operand;

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var result = Scratch.Values<byte>(length);
        var lanes = Lanes<byte>.From(operand);
        for (var i = 0; i < length; i++)
        {
            result[i] = (byte)(lanes[i] != 0 ? 0 : 1);
        }

        var nulls = InheritValidity(length, operand, context.SelectionMask);
        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// The <c>IS NULL</c> … <c>IS NOT FALSE</c> family: predicates on a value that are never themselves
/// NULL (<c>02-ir.md</c> §6). The null-ness tests accept any type; the truth tests accept BOOL.
/// </summary>
internal sealed class IsPredicateExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;
    private readonly FunctionId _function;
    private readonly bool _readsValue;

    public IsPredicateExpr(ChalkType type, FunctionId function, IVectorExpr operand)
        : base(type)
    {
        _function = function;
        _operand = operand;
        _readsValue = function is FunctionId.IsTrue or FunctionId.IsNotTrue
            or FunctionId.IsFalse or FunctionId.IsNotFalse;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var result = Scratch.Values<byte>(length);
        var lanes = _readsValue ? Lanes<byte>.From(operand) : default;

        for (var i = 0; i < length; i++)
        {
            var valid = operand.IsScalar
                ? !operand.Scalar.IsNull
                : operand.View.IsValid(i);
            var answer = _function switch
            {
                FunctionId.IsNull => !valid,
                FunctionId.IsNotNull => valid,
                FunctionId.IsTrue => valid && lanes[i] != 0,
                FunctionId.IsNotTrue => !(valid && lanes[i] != 0),
                FunctionId.IsFalse => valid && lanes[i] == 0,
                _ => !(valid && lanes[i] == 0),
            };
            result[i] = (byte)(answer ? 1 : 0);
        }

        Scratch.NoValidity();
        return Scratch.Finish(length, 0);
    }
}
