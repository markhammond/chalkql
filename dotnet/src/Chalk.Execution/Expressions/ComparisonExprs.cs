using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// A two-operand predicate producing the engine's byte-per-row BOOL. Validity is the intersection of
/// the operands' (§6.4); only the value loop differs between the layouts.
/// </summary>
internal abstract class BinaryPredicateExpr : VectorExprBase
{
    protected BinaryPredicateExpr(ChalkType type, IVectorExpr left, IVectorExpr right)
        : base(type)
    {
        Left = left;
        Right = right;
    }

    protected IVectorExpr Left { get; }

    protected IVectorExpr Right { get; }

    public override Vector Evaluate(EvalContext context)
    {
        var left = Left.Evaluate(context);
        var right = Right.Evaluate(context);
        var length = context.Length;
        Compute(left, right, Scratch.Values<byte>(length), length);
        var nulls = IntersectValidity(length, left, right, context.SelectionMask);
        return Scratch.Finish(length, nulls);
    }

    protected abstract void Compute(in Vector left, in Vector right, Span<byte> result, int length);
}

/// <summary>Comparison of a fixed-width numeric or boolean layout.</summary>
internal sealed class ComparisonExpr<T, TOp> : BinaryPredicateExpr
    where T : unmanaged
    where TOp : struct, IPredicateOp<T>
{
    public ComparisonExpr(ChalkType type, IVectorExpr left, IVectorExpr right)
        : base(type, left, right)
    {
    }

    protected override void Compute(in Vector left, in Vector right, Span<byte> result, int length)
    {
        var a = Lanes<T>.From(left);
        var b = Lanes<T>.From(right);
        for (var i = 0; i < length; i++)
        {
            result[i] = (byte)(TOp.Apply(a[i], b[i]) ? 1 : 0);
        }
    }
}

/// <summary>
/// Comparison of STRING and BINARY. Both order by bytes, which for UTF-8 is Unicode code point order
/// (<c>02-ir.md</c> §3) — no collation, by design (A7).
/// </summary>
internal sealed class VarBytesComparisonExpr<TResult> : BinaryPredicateExpr
    where TResult : struct, ICompareResultOp
{
    public VarBytesComparisonExpr(ChalkType type, IVectorExpr left, IVectorExpr right)
        : base(type, left, right)
    {
    }

    protected override void Compute(in Vector left, in Vector right, Span<byte> result, int length)
    {
        var a = VarOperand.From(left);
        var b = VarOperand.From(right);
        for (var i = 0; i < length; i++)
        {
            result[i] = (byte)(TResult.Apply(a[i].SequenceCompareTo(b[i])) ? 1 : 0);
        }
    }
}

/// <summary>Comparison of the 16-byte layouts: UUID by byte order, DECIMAL by unscaled magnitude.</summary>
internal sealed class FixedBytesComparisonExpr<TResult> : BinaryPredicateExpr
    where TResult : struct, ICompareResultOp
{
    private readonly bool _decimal;

    public FixedBytesComparisonExpr(ChalkType type, IVectorExpr left, IVectorExpr right, bool isDecimal)
        : base(type, left, right) => _decimal = isDecimal;

    protected override void Compute(in Vector left, in Vector right, Span<byte> result, int length)
    {
        var a = RawLanes.From(left, 16);
        var b = RawLanes.From(right, 16);
        for (var i = 0; i < length; i++)
        {
            var comparison = _decimal ? Decimals.Compare(a[i], b[i]) : a[i].SequenceCompareTo(b[i]);
            result[i] = (byte)(TResult.Apply(comparison) ? 1 : 0);
        }
    }
}

/// <summary>
/// <c>IS [NOT] DISTINCT FROM</c>: never NULL, NULLs compare equal to each other, and NaN compares
/// equal to NaN (<c>02-ir.md</c> §3). That last rule is why this cannot reuse the <c>=</c> kernel.
/// </summary>
internal abstract class DistinctFromExpr : VectorExprBase
{
    protected DistinctFromExpr(ChalkType type, IVectorExpr left, IVectorExpr right, bool negate)
        : base(type)
    {
        Left = left;
        Right = right;
        Negate = negate;
    }

    protected IVectorExpr Left { get; }

    protected IVectorExpr Right { get; }

    /// <summary>True for <c>IS NOT DISTINCT FROM</c>.</summary>
    protected bool Negate { get; }

    public override Vector Evaluate(EvalContext context)
    {
        var left = Left.Evaluate(context);
        var right = Right.Evaluate(context);
        var length = context.Length;
        Compute(left, right, Scratch.Values<byte>(length), length);
        Scratch.NoValidity();
        return Scratch.Finish(length, 0);
    }

    protected byte Result(bool leftValid, bool rightValid, bool valuesEqual)
    {
        var distinct = leftValid != rightValid || (leftValid && !valuesEqual);
        return (byte)(distinct != Negate ? 1 : 0);
    }

    protected abstract void Compute(in Vector left, in Vector right, Span<byte> result, int length);
}

/// <summary>The fixed-width instantiation. <c>T.Equals</c> is total, so NaN equals NaN here.</summary>
internal sealed class DistinctFromExpr<T> : DistinctFromExpr
    where T : unmanaged, IEquatable<T>
{
    public DistinctFromExpr(ChalkType type, IVectorExpr left, IVectorExpr right, bool negate)
        : base(type, left, right, negate)
    {
    }

    protected override void Compute(in Vector left, in Vector right, Span<byte> result, int length)
    {
        var a = Lanes<T>.From(left);
        var b = Lanes<T>.From(right);
        for (var i = 0; i < length; i++)
        {
            var leftValid = a.IsValid(i);
            var rightValid = b.IsValid(i);
            result[i] = Result(leftValid, rightValid, leftValid && rightValid && a[i].Equals(b[i]));
        }
    }
}

/// <summary>The STRING and BINARY instantiation.</summary>
internal sealed class VarBytesDistinctFromExpr : DistinctFromExpr
{
    public VarBytesDistinctFromExpr(ChalkType type, IVectorExpr left, IVectorExpr right, bool negate)
        : base(type, left, right, negate)
    {
    }

    protected override void Compute(in Vector left, in Vector right, Span<byte> result, int length)
    {
        var a = VarOperand.From(left);
        var b = VarOperand.From(right);
        for (var i = 0; i < length; i++)
        {
            var leftValid = a.IsValid(i);
            var rightValid = b.IsValid(i);
            result[i] = Result(leftValid, rightValid, leftValid && rightValid && a[i].SequenceEqual(b[i]));
        }
    }
}

/// <summary>The UUID and DECIMAL instantiation.</summary>
internal sealed class FixedBytesDistinctFromExpr : DistinctFromExpr
{
    public FixedBytesDistinctFromExpr(ChalkType type, IVectorExpr left, IVectorExpr right, bool negate)
        : base(type, left, right, negate)
    {
    }

    protected override void Compute(in Vector left, in Vector right, Span<byte> result, int length)
    {
        var a = RawLanes.From(left, 16);
        var b = RawLanes.From(right, 16);
        for (var i = 0; i < length; i++)
        {
            var leftValid = a.IsValid(i);
            var rightValid = b.IsValid(i);
            result[i] = Result(leftValid, rightValid, leftValid && rightValid && a[i].SequenceEqual(b[i]));
        }
    }
}

/// <summary>Reads a STRING or BINARY operand, encoding a constant's bytes only once per batch.</summary>
internal static class VarOperand
{
    public static VarLanes From(in Vector vector) => vector.IsScalar
        ? VarLanes.FromScalar(vector.Scalar.ReadBytes(), vector.Scalar.IsNull)
        : VarLanes.FromView(vector.View);
}
