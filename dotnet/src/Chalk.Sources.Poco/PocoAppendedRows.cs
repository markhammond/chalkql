using System.Collections;

namespace Chalk.Sources.Poco;

/// <summary>
/// The rows of a table an <c>Append</c> has extended: an array Chalk owns, and the number of rows of
/// it this snapshot holds (D260, <c>docs/design/35-poco-refresh.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is then a row count. The array grows amortised — doubling when it has to — and an
/// append that fits in the spare capacity writes only the new rows: the prefix is never touched, so
/// a snapshot already published keeps reading exactly the rows it was published with while a later
/// one sees more. Nothing is allocated per row beyond the array itself.
/// </para>
/// <para>
/// The buffer is Chalk's, not the host's, which is why a snapshot over one reports nothing released:
/// the collection the host gave was copied out of at the first append, and <em>that</em> is what is
/// reported released when the snapshot holding it retires.
/// </para>
/// </remarks>
internal sealed class PocoAppendedRows<T> : IReadOnlyList<T>
{
    private readonly T[] _items;

    private PocoAppendedRows(T[] items, int count)
    {
        _items = items;
        Count = count;
    }

    /// <summary>Rows this snapshot holds, which is a prefix of <see cref="Buffer"/>.</summary>
    public int Count { get; }

    /// <summary>The array itself, so the next append can see whether it has room.</summary>
    public T[] Buffer => _items;

    public T this[int index] =>
        (uint)index < (uint)Count
            ? _items[index]
            : throw new ArgumentOutOfRangeException(nameof(index), index, "row is outside this snapshot");

    /// <summary>
    /// <paramref name="existing"/> followed by <paramref name="added"/>. The existing rows keep
    /// their positions, which is what lets the permutation and the clustered copy extend rather than
    /// be rebuilt.
    /// </summary>
    public static PocoAppendedRows<T> Extend(IReadOnlyList<T> existing, int count, IReadOnlyList<T> added)
    {
        var wanted = checked(count + added.Count);

        // Room in the array this table is already using: write past the rows the current snapshot
        // holds and publish a longer view of the same buffer. A reader of the shorter view never
        // looks there.
        if (existing is PocoAppendedRows<T> owned && owned.Count == count && owned._items.Length >= wanted)
        {
            Fill(owned._items, count, added);
            return new PocoAppendedRows<T>(owned._items, wanted);
        }

        var buffer = new T[Math.Max(wanted, Math.Max(4, count * 2))];
        Copy(existing, count, buffer);
        Fill(buffer, count, added);
        return new PocoAppendedRows<T>(buffer, wanted);
    }

    /// <summary>
    /// <paramref name="rows"/> copied into an array Chalk owns, with room for as many again: a
    /// replacement prepared before its refresh (design 69 §3). Its commit writes the rows appended
    /// since its cut into the room, rather than copying the replacement again under the lock.
    /// </summary>
    public static PocoAppendedRows<T> Own(IReadOnlyList<T> rows)
    {
        var count = rows.Count;
        var buffer = new T[Math.Max(4, (int)Math.Min(Array.MaxLength, 2L * count))];
        Copy(rows, count, buffer);
        return new PocoAppendedRows<T>(buffer, count);
    }

    /// <summary>
    /// A keyed table's next rows (D367): <paramref name="existing"/>'s first <paramref name="count"/>, with
    /// the ones at <paramref name="deleted"/> taken out, the ones at <paramref name="replaced"/> replaced
    /// in place, and <paramref name="appended"/> after the rest, in an array Chalk owns with room for an
    /// eighth more, so an upsert of new keys after it appends in place. The rows between the changes
    /// are copied in bulk.
    /// </summary>
    /// <returns>The rows, and where each replaced row now stands, in <paramref name="replaced"/>'s order.</returns>
    public static (PocoAppendedRows<T> Rows, int[] Added) Assemble(
        IReadOnlyList<T> existing, int count, int[] deleted, (int Position, T Row)[] replaced, T[] appended)
    {
        var kept = count - deleted.Length;
        var wanted = checked(kept + appended.Length);
        var buffer = new T[(int)Math.Min(Array.MaxLength, wanted + Math.Max(16L, wanted / 8L))];
        var source = existing switch
        {
            T[] array => array,
            PocoAppendedRows<T> owned => owned._items,
            _ => null,
        };

        // The rows between deleted ones keep their order, each block moved up past the deletes before it.
        var write = 0;
        var from = 0;
        foreach (var gone in deleted.Append(count))
        {
            var length = gone - from;
            if (length > 0)
            {
                if (source is not null)
                {
                    Array.Copy(source, from, buffer, write, length);
                }
                else if (existing is List<T> list)
                {
                    list.CopyTo(from, buffer, write, length);
                }
                else
                {
                    for (var i = 0; i < length; i++)
                    {
                        buffer[write + i] = existing[from + i];
                    }
                }
            }

            write += length;
            from = gone + 1;
        }

        var added = new int[replaced.Length];
        var passed = 0;
        for (var i = 0; i < replaced.Length; i++)
        {
            var (position, row) = replaced[i];
            while (passed < deleted.Length && deleted[passed] < position)
            {
                passed++;
            }

            added[i] = position - passed;
            buffer[added[i]] = row;
        }

        Array.Copy(appended, 0, buffer, kept, appended.Length);
        return (new PocoAppendedRows<T>(buffer, wanted), added);
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _items[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The rows a list already holds, in bulk where the shape allows it.</summary>
    private static void Copy(IReadOnlyList<T> existing, int count, T[] into)
    {
        switch (existing)
        {
            case T[] array:
                Array.Copy(array, into, count);
                return;
            case PocoAppendedRows<T> owned:
                Array.Copy(owned._items, into, count);
                return;
            case List<T> list:
                list.CopyTo(0, into, 0, count);
                return;
            default:
                for (var i = 0; i < count; i++)
                {
                    into[i] = existing[i];
                }

                return;
        }
    }

    private static void Fill(T[] buffer, int at, IReadOnlyList<T> added)
    {
        switch (added)
        {
            case T[] array:
                Array.Copy(array, 0, buffer, at, array.Length);
                return;
            case List<T> list:
                list.CopyTo(0, buffer, at, list.Count);
                return;
            default:
                for (var i = 0; i < added.Count; i++)
                {
                    buffer[at + i] = added[i];
                }

                return;
        }
    }
}

/// <summary>
/// Rows <c>[from, from + count)</c> of a table's rows, read in place: the rows the table took since a
/// prepared replacement's cut, which the replacement's commit appends to it (design 69 §3).
/// </summary>
internal sealed class PocoRowSlice<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _rows;
    private readonly int _from;

    public PocoRowSlice(IReadOnlyList<T> rows, int from, int count)
    {
        _rows = rows;
        _from = from;
        Count = count;
    }

    public int Count { get; }

    public T this[int index] =>
        (uint)index < (uint)Count
            ? _rows[_from + index]
            : throw new ArgumentOutOfRangeException(nameof(index), index, "row is outside this slice");

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _rows[_from + i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
