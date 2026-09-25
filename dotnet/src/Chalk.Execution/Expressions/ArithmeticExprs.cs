using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Two-operand arithmetic over a fixed-width numeric layout. NULL lanes are skipped rather than
/// computed and discarded: their garbage would make a checked add overflow or a divide raise (§6.4).
/// </summary>
internal sealed class BinaryNumericExpr<T, TOp> : VectorExprBase
    where T : unmanaged
    where TOp : struct, IBinaryNumericOp<T>
{
    private readonly IVectorExpr _left;
    private readonly IVectorExpr _right;

    public BinaryNumericExpr(ChalkType type, IVectorExpr left, IVectorExpr right)
        : base(type)
    {
        _left = left;
        _right = right;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var left = _left.Evaluate(context);
        var right = _right.Evaluate(context);
        var length = context.Length;
        var nulls = IntersectValidity(length, left, right, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var result = Scratch.Values<T>(length);
        var a = Lanes<T>.From(left);
        var b = Lanes<T>.From(right);

        if (valid.IsEmpty)
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = TOp.Apply(a[i], b[i]);
            }
        }
        else
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = BitUtility.GetBit(valid, i) ? TOp.Apply(a[i], b[i]) : default;
            }
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>One-operand arithmetic over a fixed-width numeric layout.</summary>
internal sealed class UnaryNumericExpr<T, TOp> : VectorExprBase
    where T : unmanaged
    where TOp : struct, IUnaryNumericOp<T>
{
    private readonly IVectorExpr _operand;

    public UnaryNumericExpr(ChalkType type, IVectorExpr operand)
        : base(type) => _operand = operand;

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var nulls = InheritValidity(length, operand, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var result = Scratch.Values<T>(length);
        var a = Lanes<T>.From(operand);

        if (valid.IsEmpty)
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = TOp.Apply(a[i]);
            }
        }
        else
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = BitUtility.GetBit(valid, i) ? TOp.Apply(a[i]) : default;
            }
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// DECIMAL arithmetic, computed in <see cref="decimal"/> and rescaled half-even to the IR's result
/// scale (§6.4). Overflow of either the 96-bit mantissa or the declared precision raises.
/// </summary>
internal sealed class DecimalArithmeticExpr : VectorExprBase
{
    private readonly FunctionId _function;
    private readonly IVectorExpr _left;
    private readonly IVectorExpr _right;
    private readonly int _leftScale;
    private readonly int _rightScale;

    public DecimalArithmeticExpr(ChalkType type, FunctionId function, IVectorExpr left, IVectorExpr right)
        : base(type)
    {
        _function = function;
        _left = left;
        _right = right;
        _leftScale = left.Type.Scale;
        _rightScale = right.Type.Scale;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var left = _left.Evaluate(context);
        var right = _right.Evaluate(context);
        var length = context.Length;
        var nulls = IntersectValidity(length, left, right, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var result = Scratch.RawValues(length);
        var a = RawLanes.From(left, 16);
        var b = RawLanes.From(right, 16);

        for (var i = 0; i < length; i++)
        {
            var lane = result.Slice(i * 16, 16);
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                lane.Clear();
                continue;
            }

            var x = Decimals.Read(a[i], _leftScale);
            var y = Decimals.Read(b[i], _rightScale);
            var value = _function switch
            {
                FunctionId.Add => x + y,
                FunctionId.Subtract => x - y,
                FunctionId.Multiply => x * y,
                FunctionId.Divide => x / y,
                _ => x % y,
            };

            Decimals.Write(lane, value, Type.Precision, Type.Scale);
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>DECIMAL negation, absolute value and rounding, in the same <see cref="decimal"/> domain.</summary>
internal sealed class DecimalUnaryExpr : VectorExprBase
{
    private readonly FunctionId _function;
    private readonly IVectorExpr _operand;
    private readonly IVectorExpr? _digits;
    private readonly int _sourceScale;

    public DecimalUnaryExpr(ChalkType type, FunctionId function, IVectorExpr operand, IVectorExpr? digits)
        : base(type)
    {
        _function = function;
        _operand = operand;
        _digits = digits;
        _sourceScale = operand.Type.Scale;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var digits = _digits?.Evaluate(context);
        var length = context.Length;
        var nulls = digits is null
            ? InheritValidity(length, operand, context.SelectionMask)
            : IntersectValidity(length, operand, digits.Value, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var result = Scratch.RawValues(length);
        var a = RawLanes.From(operand, 16);
        var d = digits is null ? default : Lanes<int>.From(digits.Value);

        for (var i = 0; i < length; i++)
        {
            var lane = result.Slice(i * 16, 16);
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                lane.Clear();
                continue;
            }

            var x = Decimals.Read(a[i], _sourceScale);
            var value = _function switch
            {
                FunctionId.Negate => -x,
                FunctionId.Abs => Math.Abs(x),
                FunctionId.Floor => decimal.Floor(x),
                FunctionId.Ceil => decimal.Ceiling(x),
                _ => Rounding.Round(x, _digits is null ? 0 : d[i]),
            };

            Decimals.Write(lane, value, Type.Precision, Type.Scale);
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>Floating-point ROUND to a number of digits, which SQL rounds half away from zero.</summary>
internal sealed class RoundDigitsExpr<T> : VectorExprBase
    where T : unmanaged, System.Numerics.IFloatingPoint<T>
{
    private readonly IVectorExpr _operand;
    private readonly IVectorExpr _digits;

    public RoundDigitsExpr(ChalkType type, IVectorExpr operand, IVectorExpr digits)
        : base(type)
    {
        _operand = operand;
        _digits = digits;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var digits = _digits.Evaluate(context);
        var length = context.Length;
        var nulls = IntersectValidity(length, operand, digits, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var result = Scratch.Values<T>(length);
        var a = Lanes<T>.From(operand);
        var d = Lanes<int>.From(digits);

        for (var i = 0; i < length; i++)
        {
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                result[i] = default;
                continue;
            }

            result[i] = Rounding.Round(a[i], d[i]);
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// ROUND to a signed number of digits, half away from zero as SQL rounds, on the exact value (F134,
/// <see cref="Numeric.ExactRounding"/>): a negative count rounds to a power of ten, and a count at or
/// beyond the value's own precision leaves it as it is.
/// </summary>
internal static class Rounding
{
    public static decimal Round(decimal value, int digits) =>
        Numeric.ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero);

    public static T Round<T>(T value, int digits)
        where T : System.Numerics.IFloatingPoint<T>
    {
        if (typeof(T) == typeof(double))
        {
            var rounded = Numeric.ExactRounding.Round(
                System.Runtime.CompilerServices.Unsafe.As<T, double>(ref value), digits, MidpointRounding.AwayFromZero);
            return System.Runtime.CompilerServices.Unsafe.As<double, T>(ref rounded);
        }

        if (typeof(T) == typeof(float))
        {
            var rounded = Numeric.ExactRounding.Round(
                System.Runtime.CompilerServices.Unsafe.As<T, float>(ref value), digits, MidpointRounding.AwayFromZero);
            return System.Runtime.CompilerServices.Unsafe.As<float, T>(ref rounded);
        }

        throw new Chalk.Sources.UnsupportedFeatureException(
            $"ROUND over CLR type {typeof(T).Name}",
            "ROUND with a digit count is computed for double and float lanes.");
    }
}
