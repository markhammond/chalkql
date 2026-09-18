using Chalk.Execution.Operators;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// Plan-shaped scratch that lives in the execution's arena rather than on the heap
/// (<c>docs/design/15-zero-allocation-execution.md</c> §4, D64).
/// </summary>
/// <remarks>
/// Before step 20 an expression node's <c>VectorScratch</c> and an operator's <c>ColumnCopier</c>
/// grew GC arrays sized to the batch, and rebuilding the tree per execution therefore cost 4.3 bytes
/// per row — which is what kept the warm operator-tree slot of ADR 0011 §3 alive. They now rent from
/// the arena at the top of an execution and return in its <c>finally</c>, so a fresh tree allocates
/// only its (small) node objects.
/// </remarks>
internal interface IArenaScratch
{
    /// <summary>Points the scratch at this execution's arena. Nothing is rented yet.</summary>
    void Acquire(ExecutionArena arena);

    /// <summary>Returns everything rented, whether the run ended, threw or was abandoned.</summary>
    void Release();
}

/// <summary>
/// Which <see cref="OperatorContext"/> a scratch object created right now belongs to.
/// </summary>
/// <remarks>
/// An expression tree is built by <c>ExpressionCompiler</c> through forty-odd node constructors, none
/// of which has any reason to know about arenas. Rather than thread a context through all of them,
/// the tree build declares the context ambiently for the duration: building is synchronous and
/// single-threaded (D22), the scope is opened and closed by <c>PlanCompiler</c> around one tree, and
/// a scratch created outside a scope simply falls back to the heap, which is what the unit tests do.
/// </remarks>
internal sealed class ScratchScope : IDisposable
{
    [ThreadStatic]
    private static OperatorContext? _current;

    private readonly OperatorContext? _previous;
    private bool _closed;

    public ScratchScope(OperatorContext context)
    {
        _previous = _current;
        _current = context;
    }

    /// <summary>Registers <paramref name="scratch"/> with the context being built, if there is one.</summary>
    public static void Register(IArenaScratch scratch) => _current?.RegisterScratch(scratch);

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _current = _previous;
    }
}

/// <summary>
/// One growable byte buffer backed by the arena, with the heap as a fallback when no execution owns
/// it. Growing swaps the rental rather than allocating beside it, so the arena still owns every byte.
///
/// Every rental must either be returned to the arena or explicitly
/// relinquished to GC ownership before the execution ends.
/// </summary>
/// <remarks>
/// A mutable struct on purpose, and always a field: a copier holds five of these and a fresh operator
/// tree holds dozens of copiers, so an object apiece would be a measurable part of the fixed
/// per-execution cost §5 gates. Never copy one — call its methods on the field.
/// </remarks>
internal struct ArenaBuffer
{
    private ExecutionArena? _arena;
    private byte[] _bytes;
    private bool _pooled;

    public ArenaBuffer()
    {
        _bytes = [];
    }

    /// <summary>The bytes as they stand. Only the first <c>Length</c> of any request are meaningful.</summary>
    public readonly byte[] Bytes => _bytes;

    public readonly int Capacity => _bytes.Length;

    public void Acquire(ExecutionArena arena) => _arena = arena;

    public void Release()
    {
        if (_pooled && _arena is { } arena)
        {
            arena.Return(_bytes);
        }

        _bytes = [];
        _pooled = false;
        _arena = null;
    }

    /// <summary>Grows to at least <paramref name="bytes"/>, discarding what was there. True when it moved.</summary>
    public bool Ensure(int bytes)
    {
        if (_bytes.Length >= bytes)
        {
            return false;
        }

        Replace(bytes, keep: 0);
        return true;
    }

    /// <summary>Grows to at least <paramref name="bytes"/>, keeping the first <paramref name="keep"/>.</summary>
    public bool EnsureKeeping(int bytes, int keep)
    {
        if (_bytes.Length >= bytes)
        {
            return false;
        }

        Replace(bytes, keep);
        return true;
    }

    private void Replace(int bytes, int keep)
    {
        var wanted = Math.Max(bytes, Math.Max(64, _bytes.Length * 2));
        var grown = _arena is { } arena ? arena.Rent<byte>(wanted) : new byte[wanted];
        if (keep > 0)
        {
            _bytes.AsSpan(0, keep).CopyTo(grown);
        }

        if (_pooled && _arena is { } owner)
        {
            owner.Return(_bytes);
        }

        _bytes = grown;
        _pooled = _arena is not null;
    }

    internal byte[] RelinquishToGc()
    {
        var bytes = _bytes;

        if (bytes.Length == 0)
            return [];

        if (_pooled && _arena is { } arena)
        {
            //
            // This array is no longer owned by the arena.
            // It becomes ordinary GC-managed memory.
            //
            arena.RelinquishToGc(bytes);
        }

        //
        // Keep _arena: the ArenaBuffer itself remains acquired and
        // its next Ensure() should rent fresh storage from that arena.
        //
        _bytes = [];
        _pooled = false;

        return bytes;
    }
    
    /// <summary>
    /// Removes this buffer's current byte rental without returning it to
    /// the arena and without changing arena accounting.
    ///
    /// The caller assumes responsibility for eventually returning it.
    /// </summary>
    internal byte[] DetachOutstanding()
    {
        var bytes =
            _bytes; // adapt field name

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                "The arena buffer has no outstanding rental.");
        }

        //
        // Do not Return() and do not RelinquishToGc().
        //
        // The rental remains charged to the arena; ownership moves to
        // PooledRecordBatch.
        //
        _bytes = [];

        return bytes;
    }
}
