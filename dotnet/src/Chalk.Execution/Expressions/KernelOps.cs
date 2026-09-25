using System.Numerics;

namespace Chalk.Execution.Expressions;

/// <summary>
/// One comparison, written once with generic math and closed at plan compilation (D11, §6.5). The
/// floating-point instantiations get IEEE semantics for free: <c>NaN == NaN</c> is false and
/// <c>NaN != NaN</c> is true, which is exactly what <c>02-ir.md</c> §3 asks for.
/// </summary>
internal interface IPredicateOp<T>
{
    static abstract bool Apply(T left, T right);
}

internal readonly struct EqOp<T> : IPredicateOp<T>
    where T : IEqualityOperators<T, T, bool>
{
    public static bool Apply(T left, T right) => left == right;
}

internal readonly struct NeOp<T> : IPredicateOp<T>
    where T : IEqualityOperators<T, T, bool>
{
    public static bool Apply(T left, T right) => left != right;
}

internal readonly struct LtOp<T> : IPredicateOp<T>
    where T : IComparisonOperators<T, T, bool>
{
    public static bool Apply(T left, T right) => left < right;
}

internal readonly struct LeOp<T> : IPredicateOp<T>
    where T : IComparisonOperators<T, T, bool>
{
    public static bool Apply(T left, T right) => left <= right;
}

internal readonly struct GtOp<T> : IPredicateOp<T>
    where T : IComparisonOperators<T, T, bool>
{
    public static bool Apply(T left, T right) => left > right;
}

internal readonly struct GeOp<T> : IPredicateOp<T>
    where T : IComparisonOperators<T, T, bool>
{
    public static bool Apply(T left, T right) => left >= right;
}

/// <summary>The same six tests, applied to a three-way comparison of two byte sequences or decimals.</summary>
internal interface ICompareResultOp
{
    static abstract bool Apply(int comparison);
}

internal readonly struct EqResult : ICompareResultOp
{
    public static bool Apply(int comparison) => comparison == 0;
}

internal readonly struct NeResult : ICompareResultOp
{
    public static bool Apply(int comparison) => comparison != 0;
}

internal readonly struct LtResult : ICompareResultOp
{
    public static bool Apply(int comparison) => comparison < 0;
}

internal readonly struct LeResult : ICompareResultOp
{
    public static bool Apply(int comparison) => comparison <= 0;
}

internal readonly struct GtResult : ICompareResultOp
{
    public static bool Apply(int comparison) => comparison > 0;
}

internal readonly struct GeResult : ICompareResultOp
{
    public static bool Apply(int comparison) => comparison >= 0;
}

/// <summary>
/// One arithmetic operation. Integer instantiations use the checked operators, so overflow raises
/// rather than wrapping — the <c>overflow: ERROR</c> that <c>02-ir.md</c> §6 specifies. Floating-point
/// instantiations inherit the default unchecked implementations, which is IEEE.
/// </summary>
internal interface IBinaryNumericOp<T>
{
    static abstract T Apply(T left, T right);

    /// <summary>True when a NULL lane's garbage could raise: division, and everything checked.</summary>
    static abstract bool NeedsValidLanes { get; }
}

internal readonly struct AddOp<T> : IBinaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T left, T right) => checked(left + right);

    public static bool NeedsValidLanes => true;
}

internal readonly struct SubtractOp<T> : IBinaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T left, T right) => checked(left - right);

    public static bool NeedsValidLanes => true;
}

internal readonly struct MultiplyOp<T> : IBinaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T left, T right) => checked(left * right);

    public static bool NeedsValidLanes => true;
}

internal readonly struct DivideOp<T> : IBinaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T left, T right) => left / right;

    public static bool NeedsValidLanes => true;
}

internal readonly struct ModulusOp<T> : IBinaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T left, T right) => left % right;

    public static bool NeedsValidLanes => true;
}

/// <summary>One unary arithmetic operation.</summary>
internal interface IUnaryNumericOp<T>
{
    static abstract T Apply(T value);
}

internal readonly struct NegateOp<T> : IUnaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T value) => checked(-value);
}

internal readonly struct AbsOp<T> : IUnaryNumericOp<T>
    where T : INumber<T>
{
    public static T Apply(T value) => T.Abs(value);
}

internal readonly struct FloorOp<T> : IUnaryNumericOp<T>
    where T : IFloatingPoint<T>
{
    public static T Apply(T value) => T.Floor(value);
}

internal readonly struct CeilOp<T> : IUnaryNumericOp<T>
    where T : IFloatingPoint<T>
{
    public static T Apply(T value) => T.Ceiling(value);
}

/// <summary>
/// SQL ROUND is half away from zero, not .NET's default half-even (<c>02-ir.md</c> §6). With no digit
/// count there is no scaling, so <c>Round</c> is exact here on every runtime; a count goes through
/// <c>Rounding.Round</c> (F134).
/// </summary>
internal readonly struct RoundOp<T> : IUnaryNumericOp<T>
    where T : IFloatingPoint<T>
{
    public static T Apply(T value) => T.Round(value, 0, MidpointRounding.AwayFromZero);
}
