using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Joins;

/// <summary>
/// Reads, hashes and compares the equality keys of a join. The keys are field indexes into a row, so
/// there is nothing to compile: a key is a column, and its lane bytes are what gets hashed.
/// </summary>
/// <remarks>
/// <b>A NULL key never matches</b> — SQL's rule and D41's — so a row with a NULL in any key column is
/// not indexed on the build side and does not probe on the probe side. That is different from the
/// aggregate, where NULLs group together, which is why this does not reuse
/// <c>GroupKeyStore</c>'s hashing wholesale.
/// </remarks>
internal sealed class JoinKeys
{
    private readonly ColumnKind[] _kinds;
    private readonly int[] _widths;
    private readonly byte[] _left = new byte[16];
    private readonly byte[] _right = new byte[16];

    public JoinKeys(IReadOnlyList<ChalkType> types)
    {
        _kinds = [.. types.Select(ColumnKinds.Of)];
        _widths = [.. _kinds.Select(ColumnKinds.Width)];
    }

    /// <summary>
    /// The keys of an equi-join, bound: <paramref name="leftKeys"/> of the left row against
    /// <paramref name="rightKeys"/> of the right, read in the left side's layouts. Every key column
    /// on either side is one the engine hashes and compares, so a LIST or a COMPOSITE is refused by
    /// name here (D297) — the hash, merge, as-of and lookup joins all bind their keys through this.
    /// </summary>
    public static JoinKeys Bind(
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<int> leftKeys,
        IReadOnlyList<ChalkType> rightTypes,
        IReadOnlyList<int> rightKeys)
    {
        foreach (var key in leftKeys)
        {
            ColumnKinds.RequireComparable(leftTypes[key], "a join key");
        }

        foreach (var key in rightKeys)
        {
            ColumnKinds.RequireComparable(rightTypes[key], "a join key");
        }

        return new JoinKeys([.. leftKeys.Select(k => leftTypes[k])]);
    }

    public int Count => _kinds.Length;

    /// <summary>
    /// The key columns of a row set, in key order, as vectors this class can read. The array is the
    /// caller's and is reused across batches, so nothing is allocated per probe batch.
    /// </summary>
    public static void Columns(
        Vector[] into, ReadOnlySpan<ColumnView> columns, IReadOnlyList<int> keys, int length)
    {
        for (var k = 0; k < keys.Count; k++)
        {
            into[k] = Vector.FromView(columns[keys[k]], length);
        }
    }

    /// <summary>True when any key column of this row is NULL, which is a row that never matches.</summary>
    public bool AnyNull(Vector[] keys, int row)
    {
        for (var k = 0; k < keys.Length; k++)
        {
            if (!LaneAccess.IsValid(keys[k], row))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The row's key hash. Only meaningful when <see cref="AnyNull"/> is false.</summary>
    public ulong Hash(Vector[] keys, int row)
    {
        Span<byte> scratch = _left;
        ulong hash = 0;
        for (var k = 0; k < keys.Length; k++)
        {
            var lane = LaneAccess.Read(keys[k], row, _kinds[k], _widths[k], scratch);
            var value = Hashing.Bytes(lane);
            hash = k == 0 ? Hashing.Mix(value) : Hashing.Combine(hash, value);
        }

        return hash;
    }

    /// <summary>Whether two rows have equal keys. Neither may hold a NULL key.</summary>
    public bool Equal(Vector[] left, int leftRow, Vector[] right, int rightRow)
    {
        Span<byte> leftScratch = _left;
        Span<byte> rightScratch = _right;
        for (var k = 0; k < left.Length; k++)
        {
            var a = LaneAccess.Read(left[k], leftRow, _kinds[k], _widths[k], leftScratch);
            var b = LaneAccess.Read(right[k], rightRow, _kinds[k], _widths[k], rightScratch);
            if (!a.SequenceEqual(b))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Orders two rows' keys, for a merge join. <paramref name="ordering"/> says which direction and
    /// where NULLs sit, which is the ordering both inputs claimed and the merge relies on.
    /// </summary>
    public int Compare(
        Vector[] left, int leftRow, Vector[] right, int rightRow, JoinKeyOrder ordering)
    {
        Span<byte> leftScratch = _left;
        Span<byte> rightScratch = _right;
        for (var k = 0; k < left.Length; k++)
        {
            var leftValid = LaneAccess.IsValid(left[k], leftRow);
            var rightValid = LaneAccess.IsValid(right[k], rightRow);
            int result;
            if (!leftValid || !rightValid)
            {
                if (leftValid == rightValid)
                {
                    continue;
                }

                // A NULL sorts where the collation says, whatever the direction does to the values.
                result = ordering.NullsFirst(k) ? (leftValid ? 1 : -1) : (leftValid ? -1 : 1);
                return result;
            }
            else
            {
                var a = LaneAccess.Read(left[k], leftRow, _kinds[k], _widths[k], leftScratch);
                var b = LaneAccess.Read(right[k], rightRow, _kinds[k], _widths[k], rightScratch);
                result = LaneComparer.Compare(_kinds[k], a, b);
                if (ordering.Descending(k))
                {
                    result = -result;
                }
            }

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}

/// <summary>The direction and NULL placement of each merge-join key, from the inputs' collations.</summary>
internal sealed class JoinKeyOrder
{
    private readonly bool[] _descending;
    private readonly bool[] _nullsFirst;

    public JoinKeyOrder(IReadOnlyList<SortDirection> directions)
    {
        _descending = [.. directions.Select(
            d => d is SortDirection.DescNullsFirst or SortDirection.DescNullsLast)];
        _nullsFirst = [.. directions.Select(
            d => d is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst)];
    }

    public bool Descending(int key) => _descending[key];

    public bool NullsFirst(int key) => _nullsFirst[key];
}
