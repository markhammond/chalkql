using System.Collections.Concurrent;
using Chalk.Ir;
using Type = System.Type;

namespace Chalk.Sources;

public static class SourceValueOrder
{
    public interface ISourceValueComparer
    {
        int Compare(object? left, object? right);
    }
    
    private static readonly ConcurrentDictionary<RuntimeTypeHandle, Func<bool, bool, ISourceValueComparer>>
        Factories = new();

    /// <summary>
    /// Creates a comparer specialised for the CLR type represented by the supplied non-null value.
    /// The returned comparer has NULL placement and sort direction baked into it.
    /// </summary>
    public static SourceValueOrder.ISourceValueComparer CreateComparer(
        object value,
        SortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(value);

        var type = value.GetType();

        var factory = Factories.GetOrAdd(
            type.TypeHandle,
            static handle => CreateFactory(Type.GetTypeFromHandle(handle)!));

        return factory(IsNullsFirst(direction), IsDescending(direction));
    }

    /// <summary>
    /// Creates a comparer when no non-null value exists. This is necessarily the generic path because
    /// an all-NULL column carries no runtime type information.
    /// </summary>
    public static SourceValueOrder.ISourceValueComparer CreateUntypedComparer(SortDirection direction) =>
        new UntypedComparer(IsNullsFirst(direction), IsDescending(direction));

    /// <summary>
    /// Orders two values of one key in the given direction. Kept for callers outside PermutationIndex.
    /// </summary>
    public static int Compare(
        object? left,
        object? right,
        SortDirection direction)
    {
        if (left is null || right is null)
        {
            if (left is null && right is null)
                return 0;

            var nullsFirst = IsNullsFirst(direction);
            return (left is null) == nullsFirst ? -1 : 1;
        }

        var comparison = CompareAscending(left, right);
        return IsDescending(direction) ? -comparison : comparison;
    }

    /// <summary>
    /// Orders two non-null values ascending.
    /// </summary>
    public static int CompareValues(object left, object right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return CompareAscending(left, right);
    }

    private static int CompareAscending(object left, object right)
    {
        if (left is Utf8String text8)
        {
            return text8.CompareTo(AsUtf8(right));
        }

        if (left is string text)
        {
            return right is Utf8String other8
                ? -other8.CompareTo(Utf8String.FromString(text))
                : string.CompareOrdinal(text, (string)right);
        }

        if (left is double d)
        {
            return CompareReal(d, ToDouble(right));
        }

        if (left is float f)
        {
            return CompareReal(f, ToDouble(right));
        }

        if (left is ReadOnlyMemory<byte> bytes)
        {
            return bytes.Span.SequenceCompareTo(AsSpan(right));
        }

        if (left is byte[] bytesArray)
        {
            return ((ReadOnlySpan<byte>)bytesArray).SequenceCompareTo(AsSpan(right));
        }

        return ((IComparable)left).CompareTo(right);
    }

    private static Func<bool, bool, ISourceValueComparer> CreateFactory(Type type)
    {
        if (type == typeof(int))
            return static (n, d) => new Int32Comparer(n, d);

        if (type == typeof(long))
            return static (n, d) => new Int64Comparer(n, d);

        if (type == typeof(short))
            return static (n, d) => new Int16Comparer(n, d);

        if (type == typeof(byte))
            return static (n, d) => new ByteComparer(n, d);

        if (type == typeof(uint))
            return static (n, d) => new UInt32Comparer(n, d);

        if (type == typeof(ulong))
            return static (n, d) => new UInt64Comparer(n, d);

        if (type == typeof(ushort))
            return static (n, d) => new UInt16Comparer(n, d);

        if (type == typeof(sbyte))
            return static (n, d) => new SByteComparer(n, d);

        if (type == typeof(float))
            return static (n, d) => new FloatComparer(n, d);

        if (type == typeof(double))
            return static (n, d) => new DoubleComparer(n, d);

        if (type == typeof(decimal))
            return static (n, d) => new DecimalComparer(n, d);

        if (type == typeof(DateTime))
            return static (n, d) => new DateTimeComparer(n, d);

        if (type == typeof(DateTimeOffset))
            return static (n, d) => new DateTimeOffsetComparer(n, d);

        if (type == typeof(Guid))
            return static (n, d) => new GuidComparer(n, d);

        if (type == typeof(string))
            return static (n, d) => new StringComparer(n, d);

        if (type == typeof(Utf8String))
            return static (n, d) => new Utf8Comparer(n, d);

        if (type == typeof(byte[]))
            return static (n, d) => new ByteArrayComparer(n, d);

        if (type == typeof(ReadOnlyMemory<byte>))
            return static (n, d) => new ReadOnlyMemoryByteComparer(n, d);

        return (nullsFirst, descending) =>
            new ComparableComparer(type, nullsFirst, descending);
    }

    private static bool IsDescending(SortDirection direction) =>
        direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast;

    private static bool IsNullsFirst(SortDirection direction) =>
        direction is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst;

    private static int NullComparison(object? left, object? right, bool nullsFirst)
    {
        if (left is null)
        {
            if (right is null)
                return 0;

            return nullsFirst ? -1 : 1;
        }

        return right is null
            ? nullsFirst ? 1 : -1
            : int.MinValue;
    }

    private abstract class ComparerBase : ISourceValueComparer
    {
        protected readonly bool NullsFirst;
        protected readonly bool Descending;

        protected ComparerBase(bool nullsFirst, bool descending)
        {
            NullsFirst = nullsFirst;
            Descending = descending;
        }

        public int Compare(object? left, object? right)
        {
            var nullResult = NullComparison(left, right, NullsFirst);
            if (nullResult != int.MinValue)
                return nullResult;

            var result = CompareNonNull(left!, right!);
            return Descending ? -result : result;
        }

        protected abstract int CompareNonNull(object left, object right);
    }

    private sealed class Int32Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (int)left;
            var b = (int)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class Int64Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (long)left;
            var b = (long)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class Int16Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (short)left;
            var b = (short)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class ByteComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (byte)left;
            var b = (byte)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class UInt32Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (uint)left;
            var b = (uint)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class UInt64Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (ulong)left;
            var b = (ulong)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class UInt16Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (ushort)left;
            var b = (ushort)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class SByteComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            var a = (sbyte)left;
            var b = (sbyte)right;
            return a < b ? -1 : a > b ? 1 : 0;
        }
    }

    private sealed class FloatComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            CompareReal((float)left, (float)right);
    }

    private sealed class DoubleComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            CompareReal((double)left, (double)right);
    }

    private sealed class DecimalComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((decimal)left).CompareTo((decimal)right);
    }

    private sealed class DateTimeComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((DateTime)left).CompareTo((DateTime)right);
    }

    private sealed class DateTimeOffsetComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((DateTimeOffset)left).CompareTo((DateTimeOffset)right);
    }

    private sealed class GuidComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((Guid)left).CompareTo((Guid)right);
    }

    private sealed class StringComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            string.CompareOrdinal((string)left, (string)right);
    }

    private sealed class Utf8Comparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((Utf8String)left).CompareTo((Utf8String)right);
    }

    private sealed class ByteArrayComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((byte[])left).AsSpan().SequenceCompareTo((byte[])right);
    }

    private sealed class ReadOnlyMemoryByteComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            ((ReadOnlyMemory<byte>)left).Span
                .SequenceCompareTo(((ReadOnlyMemory<byte>)right).Span);
    }

    private sealed class ComparableComparer(
        Type type,
        bool nullsFirst,
        bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right)
        {
            if (left.GetType() != type || right.GetType() != type)
            {
                throw new InvalidCastException(
                    $"cannot compare {type.Name} with {left.GetType().Name} or {right.GetType().Name}");
            }

            return ((IComparable)left).CompareTo(right);
        }
    }

    private sealed class UntypedComparer(bool nullsFirst, bool descending)
        : ComparerBase(nullsFirst, descending)
    {
        protected override int CompareNonNull(object left, object right) =>
            CompareAscending(left, right);
    }

    private static int CompareReal(double left, double right)
    {
        if (double.IsNaN(left))
            return double.IsNaN(right) ? 0 : 1;

        if (double.IsNaN(right))
            return -1;

        return left < right ? -1 : left > right ? 1 : 0;
    }

    private static int CompareReal(float left, float right)
    {
        if (float.IsNaN(left))
            return float.IsNaN(right) ? 0 : 1;

        if (float.IsNaN(right))
            return -1;

        return left < right ? -1 : left > right ? 1 : 0;
    }

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        _ => throw new InvalidCastException(
            $"cannot compare a {value.GetType().Name} with a floating-point value"),
    };

    private static Utf8String AsUtf8(object value) => value switch
    {
        Utf8String other => other,
        string text => Utf8String.FromString(text),
        _ => throw new InvalidCastException(
            $"cannot compare a {value.GetType().Name} with a Utf8String"),
    };

    private static ReadOnlySpan<byte> AsSpan(object value) => value switch
    {
        ReadOnlyMemory<byte> memory => memory.Span,
        byte[] bytes => bytes,
        _ => throw new InvalidCastException(
            $"cannot compare a {value.GetType().Name} with BINARY"),
    };
}