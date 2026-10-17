using System.Buffers;
using System.Runtime.CompilerServices;

namespace Chalk.Sources.Poco;

/// <summary>
/// Where a table's row arrays come from and go back to (F245): the pool the host declared the table
/// with (<see cref="PocoTableBuilder{T}.ArrayPool"/>), or one the table keeps of its own. An array goes
/// back only when no snapshot reads it.
/// </summary>
/// <remarks>
/// A table's own pool keeps two arrays of a size: the one its snapshots read, and the one it grows
/// into. It keeps only arrays as long as the longest the table has rented: one the table outgrew is
/// left to the GC, so that a table that grew does not hold an array of every size it passed. A host's
/// pool is given back every array, as the host decides what it keeps. An array the host handed over
/// goes back to the host's pool when it declared one, and is otherwise reported released, as a
/// collection a <c>Replace</c> took over is. Arrays of rows that hold references are cleared as they
/// go back, so that a pooled array keeps no row alive.
/// </remarks>
internal sealed class PocoRowArrays<T>
{
    private const int Kept = 2;

    private readonly ArrayPool<T> _pool;
    private readonly bool _hostsPool;

    /// <summary>The longest array the table has rented: what its own pool keeps.</summary>
    private int _longest;

    public PocoRowArrays(ArrayPool<T>? pool)
    {
        _pool = pool ?? ArrayPool<T>.Create(1 << 30, Kept);
        _hostsPool = pool is not null;
    }

    /// <summary>Reports an array the host handed over that has no pool to go back to: set by the table.</summary>
    public Action<T[]>? Report { get; set; }

    /// <summary>A buffer of at least <paramref name="length"/> rows, rented from the pool.</summary>
    public PocoRowBuffer<T> Rent(int length)
    {
        var array = _pool.Rent(length);
        if (array.Length > Volatile.Read(ref _longest))
        {
            Volatile.Write(ref _longest, array.Length);
        }

        return new PocoRowBuffer<T>(array, this, host: false);
    }

    /// <summary>A buffer over an array the host handed over.</summary>
    public PocoRowBuffer<T> Adopt(T[] array) => new(array, this, host: true);

    /// <summary><paramref name="array"/>, which no snapshot reads any more, back where it came from.</summary>
    internal void GiveBack(T[] array, bool host)
    {
        if (host && !_hostsPool)
        {
            Report?.Invoke(array);
            return;
        }

        if (!_hostsPool && array.Length < Volatile.Read(ref _longest))
        {
            // Outgrown: the table rents longer arrays now, and this one would only be held.
            return;
        }

        _pool.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
    }
}

/// <summary>
/// One array of a table's rows, and how many row sets read it: the snapshots an append extended share
/// it, each seeing as many rows as it holds, and a prepared replacement holds one too. The array goes
/// back when the last of them lets go (F245).
/// </summary>
internal sealed class PocoRowBuffer<T>
{
    private readonly PocoRowArrays<T> _arrays;
    private readonly bool _host;
    private int _holders;

    public PocoRowBuffer(T[] items, PocoRowArrays<T> arrays, bool host)
    {
        Items = items;
        _arrays = arrays;
        _host = host;
    }

    /// <summary>The array.</summary>
    public T[] Items { get; }

    /// <summary>Whether the array is the host's, handed over, rather than rented.</summary>
    public bool Host => _host;

    /// <summary>A row set takes a hold as it is made over the array, before any other can let go.</summary>
    public void Hold() => Interlocked.Increment(ref _holders);

    /// <summary>A row set lets go, exactly once; the last one out gives the array back.</summary>
    public void LetGo()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
        {
            _arrays.GiveBack(Items, _host);
        }
    }
}
