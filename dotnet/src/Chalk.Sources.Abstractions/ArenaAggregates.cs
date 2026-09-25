using System.Diagnostics.CodeAnalysis;

namespace Chalk.Sources;

/// <summary>
/// A handle to bytes an aggregate rented from its arena scope (D305): an offset and a length, two
/// integers a struct state can hold where a span or an array could not. Meaningful only in the scope
/// that issued it, and read back through that scope in the same call.
/// </summary>
public readonly record struct ArenaHandle(int Offset, int Length)
{
    /// <summary>The empty buffer: a state's default.</summary>
    public static ArenaHandle Empty => default;

    public bool IsEmpty => Length == 0;
}

/// <summary>
/// The memory an <see cref="ArenaScope"/> hands out: one growing byte buffer, bump-allocated, owned by
/// whoever runs the aggregate — the execution's arena in the engine, the heap in the reference executor
/// — and released as a whole. A returned buffer is garbage until the scope is reset or released;
/// nothing moves, so a handle stays valid for the scope's life.
/// </summary>
public abstract class ArenaStore
{
    private byte[] _bytes = [];
    private int _used;

    /// <summary>Bytes handed out and not yet reset, garbage included.</summary>
    public int Used => _used;

    /// <summary>A zeroed buffer of <paramref name="length"/> bytes.</summary>
    public ArenaHandle Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Ensure(checked(_used + length));
        var buffer = new ArenaHandle(_used, length);
        _bytes.AsSpan(_used, length).Clear();
        _used += length;
        return buffer;
    }

    /// <summary>A copy of <paramref name="value"/>: the common case for text kept per group.</summary>
    public ArenaHandle Keep(ReadOnlySpan<byte> value)
    {
        var buffer = Rent(value.Length);
        value.CopyTo(Bytes(buffer));
        return buffer;
    }

    /// <summary>
    /// A buffer of <paramref name="length"/> bytes holding <paramref name="buffer"/>'s bytes: the same
    /// handle when it is no longer, a new one otherwise, the old bytes then garbage until the scope goes.
    /// </summary>
    public ArenaHandle Grow(ArenaHandle buffer, int length)
    {
        if (length <= buffer.Length)
        {
            return new ArenaHandle(buffer.Offset, length);
        }

        var grown = Rent(length);
        Bytes(buffer).CopyTo(Bytes(grown));
        return grown;
    }

    /// <summary>
    /// Gives a buffer back. Nothing is reused before the scope is reset or released — a handle never
    /// moves — so this is the host saying it is done, which a later design may act on.
    /// </summary>
    public void Return(ArenaHandle buffer)
    {
        _ = buffer;
    }

    /// <summary>The bytes behind a handle, valid for this call.</summary>
    public Span<byte> Bytes(ArenaHandle buffer) => _bytes.AsSpan(buffer.Offset, buffer.Length);

    /// <summary>Forgets every buffer: what a recomputed frame does before its first row.</summary>
    public void Reset() => _used = 0;

    /// <summary>Gives the memory back to its owner.</summary>
    public void Release()
    {
        if (_bytes.Length > 0)
        {
            Recycle(_bytes);
        }

        _bytes = [];
        _used = 0;
    }

    /// <summary>A buffer of at least <paramref name="minimum"/> bytes holding <paramref name="current"/>'s bytes.</summary>
    protected abstract byte[] Resize(byte[] current, int minimum);

    /// <summary>Gives a buffer <see cref="Resize"/> handed out back to its owner.</summary>
    protected abstract void Recycle(byte[] bytes);

    private void Ensure(int needed)
    {
        if (needed > _bytes.Length)
        {
            _bytes = Resize(_bytes, needed);
        }
    }
}

/// <summary>The store behind the reference executor's aggregates: plain arrays.</summary>
internal sealed class HeapArenaStore : ArenaStore
{
    protected override byte[] Resize(byte[] current, int minimum)
    {
        var grown = new byte[Math.Max(minimum, Math.Max(256, current.Length * 2))];
        current.CopyTo(grown, 0);
        return grown;
    }

    protected override void Recycle(byte[] bytes)
    {
    }
}

/// <summary>
/// The scope an aggregate with an arena state rents from (D305): handed to every step by reference,
/// and a <c>ref struct</c> so it can be held nowhere else. Its handles are <see cref="ArenaHandle"/>s,
/// storable in the state; its bytes are spans, valid for the call.
/// </summary>
[Experimental("CHALK001")]
public readonly ref struct ArenaScope
{
    private readonly ArenaStore _store;

    internal ArenaScope(ArenaStore store) => _store = store;

    /// <summary>Bytes handed out in this scope so far.</summary>
    public int Used => _store.Used;

    /// <summary>A zeroed buffer of <paramref name="length"/> bytes.</summary>
    public ArenaHandle Rent(int length) => _store.Rent(length);

    /// <summary>A copy of <paramref name="value"/>.</summary>
    public ArenaHandle Keep(ReadOnlySpan<byte> value) => _store.Keep(value);

    /// <summary>A buffer of <paramref name="length"/> bytes holding <paramref name="buffer"/>'s bytes.</summary>
    public ArenaHandle Grow(ArenaHandle buffer, int length) => _store.Grow(buffer, length);

    /// <summary>Gives a buffer back.</summary>
    public void Return(ArenaHandle buffer) => _store.Return(buffer);

    /// <summary>The bytes behind a handle, valid for this call.</summary>
    public Span<byte> Bytes(ArenaHandle buffer) => _store.Bytes(buffer);
}

/// <summary>The state of an empty group, rented from the scope where it needs memory.</summary>
[Experimental("CHALK001")]
public delegate TState ArenaInit<TState>(ref ArenaScope arena)
    where TState : struct;

/// <summary>One non-NULL value folded into a group's state, with the scope to keep bytes in.</summary>
[Experimental("CHALK001")]
public delegate void ArenaAccumulate<TState, in TIn>(ref TState state, TIn value, ref ArenaScope arena)
    where TState : struct
    where TIn : allows ref struct;

/// <summary>
/// Two states combined into the first: <paramref name="other"/>'s handles belong to
/// <paramref name="otherArena"/>, so what is kept is copied into <paramref name="arena"/>.
/// </summary>
[Experimental("CHALK001")]
public delegate void ArenaMerge<TState>(ref TState state, in TState other, ref ArenaScope arena, ref ArenaScope otherArena)
    where TState : struct;

/// <summary>Whether a group has an answer at all; false is a NULL, and <c>Finish</c> is not called.</summary>
[Experimental("CHALK001")]
public delegate bool ArenaHasValue<TState>(in TState state)
    where TState : struct;

/// <summary>
/// The group's answer: a fixed-width value, a record, or a <c>ReadOnlySpan&lt;byte&gt;</c> over the
/// scope for a STRING or BINARY result, copied into the result column before the next group.
/// </summary>
[Experimental("CHALK001")]
public delegate TOut ArenaFinish<TState, out TOut>(in TState state, ref ArenaScope arena)
    where TState : struct
    where TOut : allows ref struct;

/// <summary>
/// A Tier 1 aggregate whose state keeps variable-length data per group (D305): the longest label, a
/// concatenation, a sketch, a bitmap. The state is still a struct in arena memory, one per group; what
/// it cannot hold itself it rents from the <see cref="ArenaScope"/> every step receives and keeps as
/// <see cref="ArenaHandle"/> handles, so accumulation puts nothing on the heap.
/// </summary>
/// <remarks>
/// <para>
/// The scope's memory is the execution's and goes back with it; a buffer returned early is garbage
/// until then, and a frame recomputed from its rows starts from an empty scope. A handle from one scope
/// means nothing in another, which is why <see cref="Merge"/> is handed both.
/// </para>
/// <para>
/// A STRING or BINARY input arrives as a <c>ReadOnlySpan&lt;byte&gt;</c>, and a STRING or BINARY result
/// leaves as one — over the scope, or over the input — validated as UTF-8 where the column is text.
/// An empty group, or one <see cref="HasValue"/> says has no answer, is NULL.
/// </para>
/// </remarks>
[Experimental("CHALK001")]
public sealed class ArenaAggregateSpec<TState, TIn, TOut>
    where TState : struct
    where TIn : allows ref struct
    where TOut : allows ref struct
{
    /// <summary>The state of an empty group.</summary>
    public required ArenaInit<TState> Init { get; init; }

    /// <summary>Folds one non-NULL value in. NULL arguments never reach it.</summary>
    public required ArenaAccumulate<TState, TIn> Add { get; init; }

    /// <summary>Undoes one <see cref="Add"/>, where the aggregate has an exact inverse; null otherwise, and frames are recomputed.</summary>
    public ArenaAccumulate<TState, TIn>? Remove { get; init; }

    /// <summary>Combines two states, copying what the other kept into this scope. Null when the aggregate cannot be merged.</summary>
    public ArenaMerge<TState>? Merge { get; init; }

    /// <summary>Whether a group has an answer; null means every group does.</summary>
    public ArenaHasValue<TState>? HasValue { get; init; }

    /// <summary>The group's answer.</summary>
    public required ArenaFinish<TState, TOut> Finish { get; init; }
}
