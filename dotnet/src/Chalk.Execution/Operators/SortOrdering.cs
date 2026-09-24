using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Operators;

/// <summary>
/// Compares one column's values across two views. Split out from null placement and direction so
/// that the same comparer serves <c>SortOperator</c>, which compares within one concatenated column,
/// and <c>TopNOperator</c>, which compares across the batches it has retained.
/// </summary>
internal abstract class ColumnComparer
{
    public abstract int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow);

    /// <summary>The comparer for a logical type, with the ordering rules of <c>02-ir.md</c> §3.</summary>
    public static ColumnComparer For(ChalkType type) => ColumnKinds.Of(type) switch
    {
        ColumnKind.Boolean => new BooleanComparer(),
        ColumnKind.Int8 => new PrimitiveComparer<sbyte>(),
        ColumnKind.Int16 => new PrimitiveComparer<short>(),
        ColumnKind.Int32 => new PrimitiveComparer<int>(),
        ColumnKind.Int64 => new PrimitiveComparer<long>(),
        ColumnKind.Float => new FloatComparer(),
        ColumnKind.Double => new DoubleComparer(),
        ColumnKind.Utf8 or ColumnKind.Binary => new VarBytesComparer(),
        ColumnKind.Decimal128 => new DecimalComparer(),
        _ => new FixedBytesComparer(),
    };
}

internal sealed class PrimitiveComparer<T> : ColumnComparer
    where T : unmanaged, IComparable<T>
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        left.Lanes<T>()[leftRow].CompareTo(right.Lanes<T>()[rightRow]);
}

/// <summary>A source's booleans are bit-packed and a computed one is a byte; the view says which.</summary>
internal sealed class BooleanComparer : ColumnComparer
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        (left.BoolAt(leftRow) ? 1 : 0).CompareTo(right.BoolAt(rightRow) ? 1 : 0);
}

/// <summary>
/// IEEE ordering with NaN as the largest value, so ASC puts NaN last and DESC puts it first
/// (<c>02-ir.md</c> §3). <c>CompareTo</c> would put NaN first and separate -0.0 from 0.0, neither of
/// which is what SQL wants.
/// </summary>
internal sealed class DoubleComparer : ColumnComparer
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        Compare(left.Lanes<double>()[leftRow], right.Lanes<double>()[rightRow]);

    public static int Compare(double x, double y)
    {
        if (double.IsNaN(x))
        {
            return double.IsNaN(y) ? 0 : 1;
        }

        if (double.IsNaN(y))
        {
            return -1;
        }

        return x < y ? -1 : x > y ? 1 : 0;
    }
}

internal sealed class FloatComparer : ColumnComparer
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        DoubleComparer.Compare(left.Lanes<float>()[leftRow], right.Lanes<float>()[rightRow]);
}

internal sealed class VarBytesComparer : ColumnComparer
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        left.VarValue(leftRow).SequenceCompareTo(right.VarValue(rightRow));
}

internal sealed class FixedBytesComparer : ColumnComparer
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        left.RawLanes(16).Slice(leftRow * 16, 16)
            .SequenceCompareTo(right.RawLanes(16).Slice(rightRow * 16, 16));
}

internal sealed class DecimalComparer : ColumnComparer
{
    public override int Compare(in ColumnView left, int leftRow, in ColumnView right, int rightRow) =>
        Decimals.Compare(
            left.RawLanes(16).Slice(leftRow * 16, 16),
            right.RawLanes(16).Slice(rightRow * 16, 16));
}

/// <summary>
/// A multi-key ordering: column, direction and null placement per key, exactly as the IR's
/// <see cref="SortDirection"/> spells them.
/// </summary>
internal sealed class SortOrdering
{
    private readonly int[] _columns;
    private readonly bool[] _descending;
    private readonly bool[] _nullsFirst;
    private readonly ColumnComparer[] _comparers;

    public SortOrdering(IReadOnlyList<SortField> fields, RowType inputRow)
    {
        _columns = new int[fields.Count];
        _descending = new bool[fields.Count];
        _nullsFirst = new bool[fields.Count];
        _comparers = new ColumnComparer[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var column = (int)fields[i].Expr.FieldRef.Index;
            _columns[i] = column;
            _descending[i] = fields[i].Direction
                is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;
            _nullsFirst[i] = fields[i].Direction
                is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst;
            var type = ChalkType.FromProto(inputRow.Fields[column].Type);

            // D297: a sort or TopN key is bound here, so the executor's own refusal is here too.
            ColumnKinds.RequireComparable(type, "a sort key");
            _comparers[i] = ColumnComparer.For(type);
        }
    }

    public int KeyCount => _columns.Length;

    /// <summary>The input column the <paramref name="key"/>-th sort key reads.</summary>
    public int ColumnOf(int key) => _columns[key];

    public bool IsDescending(int key) => _descending[key];

    public bool NullsFirst(int key) => _nullsFirst[key];

    public ColumnComparer ComparerOf(int key) => _comparers[key];

    public int Compare(
        ReadOnlySpan<ColumnView> left, int leftRow, ReadOnlySpan<ColumnView> right, int rightRow)
    {
        for (var k = 0; k < _columns.Length; k++)
        {
            ref readonly var a = ref left[_columns[k]];
            ref readonly var b = ref right[_columns[k]];
            var aNull = !a.IsValid(leftRow);
            var bNull = !b.IsValid(rightRow);
            if (aNull || bNull)
            {
                if (aNull && bNull)
                {
                    continue;
                }

                return aNull == _nullsFirst[k] ? -1 : 1;
            }

            var comparison = _comparers[k].Compare(a, leftRow, b, rightRow);
            if (comparison != 0)
            {
                return _descending[k] ? -comparison : comparison;
            }
        }

        return 0;
    }
}
