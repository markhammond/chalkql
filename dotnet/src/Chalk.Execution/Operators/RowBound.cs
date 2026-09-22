using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Operators;

/// <summary>
/// A <c>LIMIT</c> or <c>OFFSET</c> bound: the number the planner wrote down, or the parameter this
/// execution reads it from (D285).
/// </summary>
/// <remarks>
/// <para>
/// Read once, when the execution starts, exactly as an index lookup's range bounds are: the value is
/// the same for every row, and a bound resolved per batch would be both wasteful and — for an
/// operator that counts what it has emitted — wrong.
/// </para>
/// <para>
/// A bound is an integer count and never a percentage. A negative one is refused by name before any
/// row moves, as is a NULL: both are terminal for this execution, and an operator that quietly took
/// them for zero would return an empty result that looks like an answer.
/// </para>
/// </remarks>
internal readonly struct RowBound
{
    private readonly long _value;

    /// <summary>The parameter this bound reads, or -1 when the planner wrote the number down.</summary>
    private readonly int _slot;

    /// <summary>The parameter as the plan names it, for a refusal; null for a constant.</summary>
    private readonly string? _name;

    private RowBound(long value, int slot, string? name)
    {
        _value = value;
        _slot = slot;
        _name = name;
    }

    public static RowBound Constant(long value) => new(value, -1, null);

    public static RowBound Parameter(int slot, string name) => new(0, slot, name);

    /// <summary>The bound this execution runs under.</summary>
    /// <param name="parameters">The execution's values, the statement's own first.</param>
    /// <param name="clause">The clause, for a refusal that names it: <c>LIMIT</c> or <c>OFFSET</c>.</param>
    public long Resolve(IReadOnlyList<ScalarValue> parameters, string clause)
    {
        if (_slot < 0)
        {
            return _value;
        }

        if (_slot >= parameters.Count)
        {
            throw new InvalidPlanException(
                "I-IR-5",
                clause,
                $"the {clause} bound {_name} reads parameter slot {_slot} and this execution bound "
                + $"{parameters.Count} values");
        }

        var value = parameters[_slot];
        if (value.IsNull)
        {
            throw new InvalidOperationException(
                $"NULL was bound to the {clause} bound {_name}; a {clause} bound is a count, and "
                + "NULL is not one.");
        }

        var bound = Count(value, clause);
        if (bound < 0)
        {
            throw new InvalidOperationException(
                $"{bound} was bound to the {clause} bound {_name}; a {clause} bound must be zero or "
                + "more.");
        }

        return bound;
    }

    /// <summary>
    /// A bound's value as the count it has to be.
    /// </summary>
    /// <remarks>
    /// Calcite infers an exact numeric for a bound parameter — a <c>DECIMAL(38,0)</c> in practice,
    /// since that is what an otherwise untyped parameter derives — so the integer kinds and a
    /// scale-zero decimal are what a count can arrive as. Anything else is the wrong family and is
    /// refused by name: a bound is a number of rows, never a share of them, and a value that cannot
    /// be a count must not be rounded into one.
    /// </remarks>
    private long Count(ScalarValue value, string clause)
    {
        if (value.Type.Kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64)
        {
            return value.Integer;
        }

        if (value.Type.Kind == TypeKind.Decimal)
        {
            var number = Decimals.Read(value.ReadBytes(), value.Type.Scale);
            var truncated = decimal.Truncate(number);
            if (truncated != number)
            {
                throw new InvalidOperationException(
                    $"{number} was bound to the {clause} bound {_name}; a {clause} bound is a whole "
                    + "number of rows, never a fraction or a share of them.");
            }

            return (long)truncated;
        }

        throw new InvalidOperationException(
            $"a {value.Type} was bound to the {clause} bound {_name}; a {clause} bound is a whole "
            + "number of rows, and that type cannot be one.");
    }
}
