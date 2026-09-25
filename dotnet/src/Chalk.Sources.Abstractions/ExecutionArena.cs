using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Memory;
using Array = System.Array;

namespace Chalk.Sources;

/// <summary>How an <see cref="ExecutionArena"/> is sized and bounded.</summary>
public sealed class ArenaOptions
{
    /// <summary>
    /// A hard budget for one execution, counting Arrow batch buffers and scratch together. Null — the
    /// default — is unbounded, which is what Chalk did before the arena existed.
    /// </summary>
    public long? MaxBytes { get; init; }

    /// <summary>
    /// What an idle arena keeps warm. <see cref="ExecutionArena.Trim"/> releases pooled memory beyond
    /// this to the collector, so an engine's worst case is <c>ArenaPoolSize × RetainBytes</c>.
    /// </summary>
    public long RetainBytes { get; init; } = 64L << 20;

    /// <summary>
    /// The largest rental the pools will keep, in bytes. Null — the default — pools a rental of any
    /// size, which is what lets a blocking operator's whole-input buffers serve the next execution of
    /// the same shape (D258.1). A host that would rather cap what one arena can hold on to sets it;
    /// a rental above it is served exactly, never pooled, and counted in
    /// <see cref="ExecutionArena.UnpooledReturnBytes"/> when it comes back.
    /// </summary>
    /// <remarks>
    /// This is a ceiling on <em>retention</em>, not on demand: an execution may rent as much as
    /// <see cref="MaxBytes"/> allows whatever this says, and the two are independent.
    /// </remarks>
    public long? MaxPooledRental { get; init; }
}

/// <summary>
/// Every byte one execution touches that is not a GC-managed output batch: the Arrow allocator its
/// batches are built through, and the size-classed scratch pools its operators and sources rent from
/// (<c>docs/design/08-execution-arena.md</c> §2).
/// </summary>
/// <remarks>
/// <para>
/// An arena serves <em>one execution at a time</em>. <see cref="BeginExecution"/> throws if another is
/// already running on it, and morsel parallelism (later) will give each worker its own arena. Rentals
/// themselves are guarded by a lock so that a source may build batches on its own thread, but nothing
/// about the arena is designed for contention.
/// </para>
/// <para>
/// Everything rented must be returned before the execution ends, success or failure, so an arena is
/// warm and empty between executions: <see cref="OutstandingBytes"/> is zero and the pools hold up to
/// <see cref="ArenaOptions.RetainBytes"/>. <see cref="Dispose"/> releases the lot.
/// </para>
/// <para>
/// A host may supply its own arena to <c>ChalkEngine.ExecuteAsync</c>; otherwise the engine rents one
/// from a bounded pool. Under <c>OutputMemory.Pooled</c> the batches a host receives are backed by
/// this arena's memory: they are valid until the host disposes them, and disposing or trimming the
/// arena while a batch is still held is a use-after-free waiting to happen.
/// </para>
/// </remarks>
public sealed class ExecutionArena : IDisposable
{
    /// <summary>The smallest size class. Anything shorter is rounded up to it.</summary>
    private const int MinimumClassBytes = 256;

    /// <summary>
    /// The largest length that has a power-of-two size class at all: one more doubling would not fit
    /// in an <see cref="int"/>. A request longer than this is served exactly and never pooled — an
    /// arithmetic limit rather than a policy one, which is why it is not
    /// <see cref="ArenaOptions.MaxPooledRental"/>.
    /// </summary>
    private const int LargestClassLength = 1 << 30;

    private readonly Lock _gate = new();
    private readonly Dictionary<Type, SizeClasses> _pools = [];
    private readonly ArenaMemoryAllocator _allocator;
    private readonly ArenaMemoryPool _memoryPool;

    /// <summary>
    /// The one Arrow builder the arena keeps, used as a conduit: content is staged in it and copied into
    /// <see cref="Allocator"/> memory by <c>Build</c>. Apache.Arrow 23 has no public way to wrap an
    /// <see cref="IMemoryOwner{T}"/> as an <see cref="ArrowBuffer"/>, so this is how a caller that stages
    /// in rented scratch still ends up with an owned buffer, and why no operator or source needs a builder
    /// of its own.
    /// </summary>
    private ArrowBuffer.Builder<byte>? _conduit;

    private ExecutionStats? _stats;
    private long _outstanding;
    private long _peak;
    private long _executionPeak;
    private long _retained;
    private long _rentals;
    private long _rentedBytes;
    private long _heapFallbacks;
    private long _heapFallbackBytes;
    private long _unpooledReturnBytes;
    private long _trimmedBytes;

    /// <summary>Ticks once per rental of a poolable class, and is what recency is measured in.</summary>
    private long _useStamp;
    private bool _running;
    private bool _disposed;

    public ExecutionArena(ArenaOptions? options = null)
    {
        Options = options ?? new ArenaOptions();
        if (Options.RetainBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), Options.RetainBytes, "RetainBytes cannot be negative.");
        }

        if (Options.MaxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), Options.MaxBytes, "MaxBytes must be positive when it is set.");
        }

        if (Options.MaxPooledRental is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                Options.MaxPooledRental,
                "MaxPooledRental must be positive when it is set; leave it null to pool a rental of "
                + "any size.");
        }

        _allocator = new ArenaMemoryAllocator(this);
        _memoryPool = new ArenaMemoryPool(this);
    }

    /// <summary>The options this arena was built with.</summary>
    public ArenaOptions Options { get; }

    /// <summary>Where Arrow buffers for batches built inside this execution come from.</summary>
    public MemoryAllocator Allocator => _allocator;

    /// <summary>
    /// The arena behind the runtime's pool abstraction, for code written to a <see cref="MemoryPool{T}"/>
    /// rather than to <see cref="Rent{T}"/>: a buffer rented from it is the size class behind the
    /// request, counts toward the budget, and comes back to the arena when its owner is disposed.
    /// Disposing the pool itself does nothing; the arena owns it.
    /// </summary>
    public MemoryPool<byte> Pool => _memoryPool;

    /// <summary>Bytes rented and not yet returned — batch buffers and scratch together.</summary>
    public long OutstandingBytes => Volatile.Read(ref _outstanding);

    /// <summary>The largest <see cref="OutstandingBytes"/> this arena has ever reached.</summary>
    public long PeakBytes => Volatile.Read(ref _peak);

    /// <summary>Bytes sitting idle in the pools, ready for the next rental. Bounded by <see cref="Trim"/>.</summary>
    public long RetainedBytes => Volatile.Read(ref _retained);

    /// <summary>Whether an execution is running on this arena right now.</summary>
    public bool IsRunning => Volatile.Read(ref _running);

    /// <summary>
    /// Rentals this arena has served since it was created, and the bytes they asked for. Monotonic:
    /// a caller measuring one execution takes the difference across it.
    /// </summary>
    public long Rentals => Volatile.Read(ref _rentals);

    /// <summary>Bytes charged over every rental in <see cref="Rentals"/>.</summary>
    public long RentedBytes => Volatile.Read(ref _rentedBytes);

    /// <summary>
    /// Rentals the pools could not serve, so the arena allocated a fresh array — the arena's own
    /// fall back to the heap, and the number that says whether "arena-backed" is also "allocation
    /// free". A rental above <see cref="ArenaOptions.MaxPooledRental"/>, where a host has set one, is
    /// always one of these, because such a request is served exactly and never pooled (§2.3).
    /// </summary>
    public long HeapFallbacks => Volatile.Read(ref _heapFallbacks);

    /// <summary>Bytes those fresh arrays cost the collector.</summary>
    public long HeapFallbackBytes => Volatile.Read(ref _heapFallbackBytes);

    /// <summary>
    /// Bytes handed back through <c>Return</c> that the pools refused to keep — above
    /// <see cref="ArenaOptions.MaxPooledRental"/>, or off a size class — and that therefore became
    /// garbage rather than the next rental.
    /// </summary>
    public long UnpooledReturnBytes => Volatile.Read(ref _unpooledReturnBytes);

    /// <summary>Bytes <see cref="Trim"/> has released from the pools to the collector.</summary>
    public long TrimmedBytes => Volatile.Read(ref _trimmedBytes);

    /// <summary>
    /// Claims the arena for one execution and points its high-water mark at
    /// <paramref name="stats"/>. Paired with <see cref="EndExecution"/> in a <c>finally</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another execution is already using this arena.</exception>
    public void BeginExecution(ExecutionStats? stats = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException(
                    "this ExecutionArena is already serving an execution; an arena serves one execution "
                    + "at a time, so give each concurrent execution its own arena.");
            }

            _running = true;
            _stats = stats;
            _executionPeak = _outstanding;
        }
    }

    /// <summary>
    /// Releases the claim and trims the pools back to <see cref="ArenaOptions.RetainBytes"/>. Safe to
    /// call without a matching <see cref="BeginExecution"/>.
    /// </summary>
    public void EndExecution()
    {
        lock (_gate)
        {
            _running = false;
            _stats = null;
            TrimLocked();
        }
    }

    /// <summary>
    /// Scratch for the length of one execution. The contents are undefined — this is a pool, not an
    /// allocator — and the array must come back through <see cref="Return{T}"/>.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="T"/> is any value type rather than only an unmanaged one, because the POCO
    /// source stages a BINARY column as <see cref="ReadOnlyMemory{T}"/> handles; an array of a struct
    /// that carries references is cleared on return so the pool pins nothing.
    /// </remarks>
    public T[] Rent<T>(int minimumLength)
        where T : struct
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0)
        {
            return [];
        }

        var size = Unsafe.SizeOf<T>();
        var length = ClassLength(minimumLength, size);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bytes = (long)length * size;
            Charge(bytes);
            Count(bytes);
            if (TakeLocked(typeof(T), size, length) is T[] pooled)
            {
                return pooled;
            }

            CountHeapFallback(bytes);
            return new T[length];
        }
    }

    /// <summary>Gives scratch back. Anything else is a bookkeeping error and would skew the accounting.</summary>
    public void Return<T>(T[] array)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Length == 0)
        {
            return;
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array);
        }

        var size = Unsafe.SizeOf<T>();
        lock (_gate)
        {
            Discharge((long)array.Length * size);
            PutLocked(typeof(T), size, array);
        }
    }

    /// <summary>
    /// The one reference-typed staging array the POCO source needs: a batch of <c>string</c> handles
    /// collected by the chunk loop before they are encoded as UTF-8.
    /// </summary>
    public string[] RentStrings(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0)
        {
            return [];
        }

        var length = ClassLength(minimumLength, ReferenceSize);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bytes = (long)length * ReferenceSize;
            Charge(bytes);
            Count(bytes);
            if (TakeLocked(typeof(string), ReferenceSize, length) is string[] pooled)
            {
                return pooled;
            }

            CountHeapFallback(bytes);
            return new string[length];
        }
    }

    /// <summary>Gives string staging back. The array is cleared first: a pool must not pin what it held.</summary>
    public void ReturnStrings(string[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Length == 0)
        {
            return;
        }

        Array.Clear(array);
        lock (_gate)
        {
            Discharge((long)array.Length * ReferenceSize);
            PutLocked(typeof(string), ReferenceSize, array);
        }
    }

    /// <summary>
    /// Copies <paramref name="content"/> into a buffer owned by <see cref="Allocator"/>, which the
    /// batch that receives it releases on <c>Dispose</c>. The one supported way to turn staging in
    /// rented scratch into an owned Arrow buffer (ADR 0007: the owning <see cref="ArrowBuffer"/>
    /// constructor is internal to Apache.Arrow).
    /// </summary>
    public ArrowBuffer CreateBuffer(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return ArrowBuffer.Empty;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _conduit ??= new ArrowBuffer.Builder<byte>(content.Length);
            _conduit.Resize(content.Length);
            content.CopyTo(_conduit.Span);
            return _conduit.Build(_allocator);
        }
    }

    /// <summary>
    /// Releases pooled memory beyond <see cref="ArenaOptions.RetainBytes"/>, least recently used size
    /// class first (§2.3). Outstanding rentals are untouched.
    /// </summary>
    public void Trim()
    {
        lock (_gate)
        {
            TrimLocked();
        }
    }

    /// <summary>
    /// Releases everything the arena holds. Memory still outstanding stays alive for whoever holds it
    /// but is no longer accounted for, and further rentals throw.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _stats = null;
            _pools.Clear();
            _conduit = null;
            _retained = 0;
        }
    }

    private static int ReferenceSize => IntPtr.Size;

    /// <summary>
    /// Power-of-two lengths, never smaller than <see cref="MinimumClassBytes"/>. A rental of any size
    /// has a class (§2.3, D258.1): what a blocking operator rents over its whole input is exactly what
    /// the next execution of the same shape wants back. Only a length with no next power of two, or
    /// one a host's <see cref="ArenaOptions.MaxPooledRental"/> refuses to keep, is served exactly —
    /// and then it is never pooled, so the pool can never hand back an array shorter than was asked
    /// for.
    /// </summary>
    private int ClassLength(int minimumLength, int elementSize)
    {
        if (minimumLength > LargestClassLength)
        {
            return minimumLength;
        }

        var length = (int)BitOperations.RoundUpToPowerOf2((uint)minimumLength);
        while ((long)length * elementSize < MinimumClassBytes)
        {
            length <<= 1;
        }

        return IsPoolable(length, (long)length * elementSize) ? length : minimumLength;
    }

    /// <summary>Whether an array of this shape belongs in a pool at all.</summary>
    private bool IsPoolable(int length, long bytes) =>
        bytes >= MinimumClassBytes
        && length <= LargestClassLength
        && BitOperations.IsPow2((uint)length)
        && (Options.MaxPooledRental is not { } cap || bytes <= cap);

    private Array? TakeLocked(Type element, int elementSize, int length)
    {
        // Only a size class is ever pooled, so only a size class may be served from a pool: an
        // oversized exact request shares a log2 bucket with the class just below it, and taking that
        // one would hand back an array shorter than was asked for.
        if (!IsPoolable(length, (long)length * elementSize))
        {
            return null;
        }

        var classes = ClassesFor(element, elementSize);
        var index = BitOperations.Log2((uint)length);

        // A class counts as used when it is *rented*, whether or not a pooled array was there to
        // serve it: what this execution asked for is what the next one of the same shape will ask
        // for, and that is what the trim has to keep (§2.3, D258.2).
        classes.LastUsed[index] = ++_useStamp;

        var bucket = classes.Buckets[index];
        if (bucket is null || bucket.Count == 0)
        {
            return null;
        }

        var array = bucket[^1];
        bucket.RemoveAt(bucket.Count - 1);
        _retained -= (long)length * elementSize;
        return array;
    }

    /// <summary>This element type's size classes, created on first sight. One object per type.</summary>
    private SizeClasses ClassesFor(Type element, int elementSize)
    {
        if (!_pools.TryGetValue(element, out var classes))
        {
            classes = new SizeClasses(elementSize);
            _pools[element] = classes;
        }

        return classes;
    }

    /// <summary>Books one rental. Called under <see cref="_gate"/>, like every other counter here.</summary>
    private void Count(long bytes)
    {
        Volatile.Write(ref _rentals, _rentals + 1);
        Volatile.Write(ref _rentedBytes, _rentedBytes + bytes);
    }

    /// <summary>Books a rental the pools could not serve, so the collector paid for it.</summary>
    private void CountHeapFallback(long bytes)
    {
        Volatile.Write(ref _heapFallbacks, _heapFallbacks + 1);
        Volatile.Write(ref _heapFallbackBytes, _heapFallbackBytes + bytes);
    }

    private void PutLocked(Type element, int elementSize, Array array)
    {
        var bytes = (long)array.Length * elementSize;
        if (_disposed || !IsPoolable(array.Length, bytes))
        {
            Volatile.Write(ref _unpooledReturnBytes, _unpooledReturnBytes + bytes);
            return;
        }

        var classes = ClassesFor(element, elementSize);
        var index = BitOperations.Log2((uint)array.Length);
        (classes.Buckets[index] ??= []).Add(array);
        _retained += bytes;
    }

    /// <summary>Books a rental, refusing it when it would break the budget.</summary>
    private void Charge(long bytes)
    {
        var outstanding = _outstanding + bytes;
        if (Options.MaxBytes is { } max && outstanding > max)
        {
            throw new ArenaBudgetExceededException(max, bytes, _outstanding);
        }

        Volatile.Write(ref _outstanding, outstanding);
        if (outstanding > _peak)
        {
            Volatile.Write(ref _peak, outstanding);
        }

        if (outstanding > _executionPeak)
        {
            _executionPeak = outstanding;
            _stats?.ObservePooledBytes(outstanding);
        }
    }

    private void Discharge(long bytes) => Volatile.Write(ref _outstanding, _outstanding - bytes);

    /// <summary>
    /// Releases pooled arrays until the budget holds again, taking the least recently used size class
    /// first (§2.3, D258.2). Never the largest first: the largest classes are a blocking operator's
    /// whole-input buffers, which are exactly what the next execution of the same shape asks for, and
    /// evicting them made every such execution pay the collector for them again (design 33 §3.3).
    /// </summary>
    /// <remarks>
    /// The search is over size classes — a handful of buckets per element type — rather than over
    /// rentals, and it allocates nothing: <see cref="EndExecution"/> runs it on every execution, so a
    /// list or a sort here would show up in the fixed per-execution figure the benchmark gates.
    /// </remarks>
    private void TrimLocked()
    {
        while (_retained > Options.RetainBytes)
        {
            SizeClasses? oldest = null;
            var oldestIndex = -1;
            var oldestUse = long.MaxValue;
            foreach (var classes in _pools.Values)
            {
                for (var index = 0; index < classes.Buckets.Length; index++)
                {
                    if (classes.Buckets[index] is not { Count: > 0 }
                        || classes.LastUsed[index] >= oldestUse)
                    {
                        continue;
                    }

                    oldestUse = classes.LastUsed[index];
                    oldest = classes;
                    oldestIndex = index;
                }
            }

            if (oldest is null)
            {
                // Nothing left to release: everything the budget is over by is outstanding.
                return;
            }

            var bucket = oldest.Buckets[oldestIndex]!;
            while (bucket.Count > 0 && _retained > Options.RetainBytes)
            {
                var bytes = (long)bucket[^1].Length * oldest.ElementSize;
                _retained -= bytes;
                Volatile.Write(ref _trimmedBytes, _trimmedBytes + bytes);
                bucket.RemoveAt(bucket.Count - 1);
            }
        }
    }
    
    /// <summary>
    /// Relinquishes an outstanding byte rental to the GC rather than returning it
    /// to this arena's pool.
    ///
    /// The caller becomes solely responsible for keeping the array alive. The
    /// arena no longer accounts for it and must never see it through Return().
    /// </summary>
    internal void RelinquishToGc(byte[] array)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (array.Length == 0)
            return;

        lock (_gate)
        {
            //
            // Rent<byte>() charged array.Length bytes.
            //
            Discharge(array.Length);

            //
            // Deliberately do NOT PutLocked().
            //
            // The array is now ordinary managed memory whose lifetime is
            // determined purely by GC reachability.
            //
        }
    }

    /// <summary>The pooled arrays of one element type, one bucket per power-of-two length.</summary>
    private sealed class SizeClasses
    {
        public SizeClasses(int elementSize) => ElementSize = elementSize;

        public int ElementSize { get; }

        public List<Array>?[] Buckets { get; } = new List<Array>?[32];

        /// <summary>
        /// When each class was last rented, on the arena's own counter. Zero means never, which makes
        /// a class nobody has asked for the first one the trim takes (D258.2). One array of longs per
        /// element type: the recency bookkeeping is per class, not per rental.
        /// </summary>
        public long[] LastUsed { get; } = new long[32];
    }

    /// <summary>
    /// Arrow buffers over the arena's own pools rather than <see cref="ArrayPool{T}"/>, so that a
    /// batch's memory counts toward the budget and comes back on <c>RecordBatch.Dispose()</c>.
    /// </summary>
    private sealed class ArenaMemoryAllocator : MemoryAllocator
    {
        private readonly ExecutionArena _arena;

        public ArenaMemoryAllocator(ExecutionArena arena)
            : base(alignment: 64) => _arena = arena;

        protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
        {
            if (length == 0)
            {
                bytesAllocated = 0;
                return new ExecutionArenaBuffer(_arena, [], 0);
            }

            // Arrow hands out padded buffers and expects the padding to read as zero; a pooled array
            // does not, so it is cleared here rather than leaving stale bytes where a validity
            // bitmap's tail would be.
            var rented = _arena.Rent<byte>(length);
            rented.AsSpan(0, length).Clear();
            bytesAllocated = length;
            return new ExecutionArenaBuffer(_arena, rented, length);
        }
    }

    /// <summary>
    /// <see cref="Pool"/>: the arena over the runtime's pool abstraction, on the same size classes and
    /// accounting as <see cref="Rent{T}"/>. A request of no particular size gets what the runtime's
    /// shared pool would give it.
    /// </summary>
    private sealed class ArenaMemoryPool : MemoryPool<byte>
    {
        private const int DefaultLength = 4096;

        private readonly ExecutionArena _arena;

        public ArenaMemoryPool(ExecutionArena arena) => _arena = arena;

        public override int MaxBufferSize => Array.MaxLength;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            if (minBufferSize == -1)
            {
                minBufferSize = DefaultLength;
            }
            else
            {
                ArgumentOutOfRangeException.ThrowIfNegative(minBufferSize);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(minBufferSize, MaxBufferSize);
            }

            if (minBufferSize == 0)
            {
                return new ExecutionArenaBuffer(_arena, [], 0);
            }

            var rented = _arena.Rent<byte>(minBufferSize);
            return new ExecutionArenaBuffer(_arena, rented, rented.Length);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>
    /// What <see cref="Allocator"/> and <see cref="Pool"/> hand out: an array of the arena's, given
    /// back to it once, however many times the owner is disposed.
    /// </summary>
    private sealed class ExecutionArenaBuffer : IMemoryOwner<byte>
    {
        private readonly ExecutionArena _arena;
        private readonly int _length;
        private byte[]? _array;

        public ExecutionArenaBuffer(ExecutionArena arena, byte[] array, int length)
        {
            _arena = arena;
            _array = array;
            _length = length;
        }

        public bool IsEmpty =>
            _array?.Length == 0;

        public Memory<byte> Memory => _array?.AsMemory(0, _length) ?? Memory<byte>.Empty;

        public void Dispose()
        {
            // Arrow's reference counting can reach a buffer twice when a batch and a shared slice
            // of it are both disposed; returning an array to a pool twice would corrupt it, so the
            // reference is swapped out first.
            var array = Interlocked.Exchange(ref _array, null);
            if (array is { Length: > 0 })
            {
                _arena.Return(array);
            }
        }
    }
}

/// <summary>
/// An execution asked its arena for more memory than <see cref="ArenaOptions.MaxBytes"/> allows. The
/// operator tree turns this into an <see cref="ExecutionException"/> naming the operator that asked.
/// </summary>
public sealed class ArenaBudgetExceededException : ChalkException
{
    public ArenaBudgetExceededException(long maxBytes, long requestedBytes, long outstandingBytes)
        : base($"execution exceeded its arena budget of {maxBytes} bytes: {outstandingBytes} bytes were "
            + $"already outstanding and {requestedBytes} more were asked for")
    {
        MaxBytes = maxBytes;
        RequestedBytes = requestedBytes;
        OutstandingBytes = outstandingBytes;
    }

    /// <summary>The budget that was exceeded, from <see cref="ArenaOptions.MaxBytes"/>.</summary>
    public long MaxBytes { get; }

    /// <summary>What the failing rental asked for.</summary>
    public long RequestedBytes { get; }

    /// <summary>What the arena had already handed out.</summary>
    public long OutstandingBytes { get; }
}
