using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Kleene <c>AND</c> and <c>OR</c> (<c>02-ir.md</c> §5), n-ary. Each operand moves the lane towards its
/// absorbing value — FALSE for AND, TRUE for OR — and a lane that never reaches it but saw a NULL is
/// NULL. Once every lane has been absorbed the remaining operands are not evaluated at all, which is
/// the lane-mask short circuit §6.4 asks for.
/// </summary>
internal sealed class KleeneExpr : VectorExprBase
{
    private readonly IVectorExpr[] _operands;
    private readonly bool _isAnd;
    private byte[] _sawNull = [];

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
        foreach (var operand in _operands)
        {
            var vector = operand.Evaluate(context);
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
