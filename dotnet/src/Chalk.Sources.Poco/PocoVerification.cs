using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using Chalk.Catalog;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Poco;

/// <summary>
/// D17: a declared collation or unique key that does not hold over the data is a silent wrong-answer
/// bug — the planner deletes a Sort or an Aggregate on the strength of it. One linear pass at
/// <c>Build()</c> is cheap insurance; <c>Verify(false)</c> opts out for hosts that guarantee the
/// invariants another way.
/// </summary>
/// <remarks>
/// Values are compared as the scan will emit them (see <c>PocoChunkCompiler.CompileLogicalAccessor</c>),
/// so an enum column mapped to STRING is checked in name order rather than in CLR enum order. This
/// runs once per <c>Build()</c> and is allowed to box; the scan path is the one with a budget.
/// </remarks>
internal static class PocoVerification
{
    /// <param name="from">
    /// The first row to compare with the one before it. One — every row — for a collection that has
    /// not been checked before; for an append, the first appended row, because a collation is a
    /// statement about consecutive rows and the ones before it were checked when they arrived
    /// (D260 §3). Row numbers in the failure are the collection's, either way.
    /// </param>
    public static void VerifyCollation<T>(
        string table,
        IReadOnlyList<T> rows,
        IReadOnlyList<(string Column, SortDirection Direction, Func<T, object?> Value)> keys,
        int from = 1)
    {
        var declaration = "collation ["
            + string.Join(", ", keys.Select(k => $"{k.Column} {Describe(k.Direction)}"))
            + "]";

        for (var i = Math.Max(1, from); i < rows.Count; i++)
        {
            var previous = rows[i - 1];
            var current = rows[i];
            foreach (var key in keys)
            {
                var comparison = Compare(key.Value(previous), key.Value(current), key.Direction);
                if (comparison > 0)
                {
                    throw new CatalogVerificationException(
                        table,
                        declaration,
                        i,
                        $"column '{key.Column}' goes backwards between rows {i - 1} and {i}");
                }

                if (comparison < 0)
                {
                    break;
                }
            }
        }
    }

    public static void VerifyUniqueKey<T>(
        string table,
        IReadOnlyList<T> rows,
        IReadOnlyList<string> columns,
        IReadOnlyList<Func<T, object?>> values)
    {
        var declaration = "unique key (" + string.Join(", ", columns) + ")";
        var seen = new HashSet<object?[]>(rows.Count, PocoKeyComparer.Instance);

        for (var i = 0; i < rows.Count; i++)
        {
            var key = new object?[values.Count];
            for (var k = 0; k < values.Count; k++)
            {
                key[k] = values[k](rows[i]);
            }

            if (!seen.Add(key))
            {
                throw new CatalogVerificationException(
                    table, declaration, i, $"key ({string.Join(", ", key.Select(Render))}) already occurred");
            }
        }
    }

    /// <summary>
    /// Orders two values of one key in the direction that was declared, so a positive result always
    /// means "these two rows are in the wrong order". NULL placement follows the direction, exactly
    /// as Calcite reads it (D15, <c>NullCollation.HIGH</c>).
    /// </summary>
    private static int Compare(object? left, object? right, SortDirection direction)
    {
        var descending = direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;
        var nullsFirst = direction is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst;

        if (left is null || right is null)
        {
            if (left is null && right is null)
            {
                return 0;
            }

            var leftIsNull = left is null;
            return (leftIsNull == nullsFirst) ? -1 : 1;
        }

        var comparison = CompareValues(left, right);
        return descending ? -comparison : comparison;
    }

    private static int CompareValues(object left, object right) => left switch
    {
        // D145: a Utf8String column is binary-collated, so its declared collation is verified in
        // byte order — the same order the scan emits and the planner assumes.
        Utf8String text8 => text8.CompareTo((Utf8String)right),
        string text => string.CompareOrdinal(text, (string)right),
        double d => CompareDouble(d, (double)right),
        float f => CompareDouble(f, (float)right),
        ReadOnlyMemory<byte> bytes => bytes.Span.SequenceCompareTo(((ReadOnlyMemory<byte>)right).Span),
        _ => ((IComparable)left).CompareTo(right),
    };

    /// <summary>NaN sorts last in ascending order (<c>02-ir.md</c> §3), which is not what CompareTo does.</summary>
    private static int CompareDouble(double left, double right)
    {
        if (double.IsNaN(left))
        {
            return double.IsNaN(right) ? 0 : 1;
        }

        return double.IsNaN(right) ? -1 : left.CompareTo(right);
    }

    private static string Describe(SortDirection direction) => direction switch
    {
        SortDirection.AscNullsFirst => "ASC NULLS FIRST",
        SortDirection.AscNullsLast => "ASC NULLS LAST",
        SortDirection.DescNullsFirst => "DESC NULLS FIRST",
        SortDirection.DescNullsLast => "DESC NULLS LAST",
        _ => direction.ToString(),
    };

    internal static string Render(object? value) => value switch
    {
        null => "NULL",
        Utf8String text8 => $"'{text8}'",
        ReadOnlyMemory<byte> bytes => $"0x{Convert.ToHexString(bytes.Span)}",
        string text => $"'{text}'",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Structural equality over a key tuple. NULL equals NULL here, the same rule grouping uses
    /// (§6.6): a unique key is a claim that the tuple identifies the row, and two all-NULL tuples
    /// identify nothing.
    /// </summary>
    internal sealed class PocoKeyComparer : IEqualityComparer<object?[]>
    {
        public static PocoKeyComparer Instance { get; } = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (x is null || y is null)
            {
                return ReferenceEquals(x, y);
            }

            for (var i = 0; i < x.Length; i++)
            {
                if (!ValueEquals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] values)
        {
            unchecked
            {
                // Include arity and avoid starting at zero.
                var hash = 0x9E3779B9u ^ (uint)values.Length;

                foreach (var value in values)
                {
                    var elementHash = value switch
                    {
                        null => 0xA511E9B3u,

                        ReadOnlyMemory<byte> bytes =>
                            Fold64(XxHash3.HashToUInt64(bytes.Span)),

                        _ => (uint)value.GetHashCode()
                    };

                    // Very cheap ordered tuple combine.
                    hash ^= elementHash;
                    hash = BitOperations.RotateLeft(hash, 13);
                    hash *= 0x9E3779B1u;
                }

                // One avalanche for the complete composite key.
                hash ^= hash >> 16;
                hash *= 0x85EBCA6Bu;
                hash ^= hash >> 13;
                hash *= 0xC2B2AE35u;
                hash ^= hash >> 16;

                return (int)hash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Fold64(ulong value) =>
            (uint)value ^ (uint)(value >> 32);

        private static bool ValueEquals(object? left, object? right)
        {
            if (left is ReadOnlyMemory<byte> leftBytes && right is ReadOnlyMemory<byte> rightBytes)
            {
                return leftBytes.Span.SequenceEqual(rightBytes.Span);
            }

            return Equals(left, right);
        }
    }
}
