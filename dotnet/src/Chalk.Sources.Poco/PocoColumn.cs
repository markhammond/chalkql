using System.Runtime.InteropServices;
using Apache.Arrow;
using Chalk.Catalog;
using ArrowField = Apache.Arrow.Field;

namespace Chalk.Sources.Poco;

/// <summary>
/// One SQL column of a POCO table: the catalog shape, plus the factory for the per-scan state that
/// turns rows into Arrow arrays. The column itself is immutable and shared by every concurrent scan.
/// </summary>
internal abstract class PocoColumn<T>
{
    protected PocoColumn(string name, ChalkType type)
    {
        Name = name;
        Type = type;
        Field = ArrowTypeMapping.ToArrowField(name, type);
    }

    public string Name { get; }

    public ChalkType Type { get; }

    /// <summary>The Arrow field this column contributes to a scan's output schema.</summary>
    public ArrowField Field { get; }

    /// <summary>The catalog view of this column.</summary>
    public ColumnDescriptor Descriptor => new() { Name = Name, Type = Type };

    /// <summary>
    /// Per-scan state: staging arrays rented from <paramref name="arena"/>, sized once for the batch
    /// and reused for every batch of the scan. Reuse is what keeps the scan under a byte per row
    /// (§5.3); the arena is what keeps the reuse from being retention (ADR 0012).
    /// </summary>
    /// <remarks>
    /// The writer <em>object</em> is pooled one deep per column, so a second scan of the same column
    /// costs nothing beyond re-renting the staging (ADR 0020 §1, F15). The slot is taken atomically,
    /// so two concurrent scans of one table never share a writer: whoever loses the exchange builds
    /// its own.
    /// </remarks>
    public PocoChunkWriter<T> AcquireWriter(ExecutionArena arena, int capacity)
    {
        var writer = Interlocked.Exchange(ref _spare, null) ?? CreateWriter();
        writer.Acquire(arena, capacity);
        return writer;
    }

    /// <summary>Hands a writer's staging back to the arena and the writer itself to the pool.</summary>
    public void ReleaseWriter(PocoChunkWriter<T> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Release();
        Volatile.Write(ref _spare, writer);
    }

    /// <summary>A writer with no staging yet. <see cref="AcquireWriter"/> is what a scan calls.</summary>
    public abstract PocoChunkWriter<T> CreateWriter();

    private PocoChunkWriter<T>? _spare;

    /// <summary>The value this column will emit, boxed. Verification only; see <see cref="PocoChunkCompiler"/>.</summary>
    public abstract Func<T, object?> CompileLogicalAccessor();
}

/// <summary>
/// The per-scan half of a column. Validity is collected byte-per-row while the chunk loop runs and
/// packed into Arrow's bitmap once per batch (§5.3).
/// </summary>
/// <remarks>
/// Everything a writer stages is rented from the scan's <see cref="ExecutionArena"/> and returned by
/// <see cref="Release"/>, so a column that has been scanned retains nothing (ADR 0012, replacing
/// ADR 0007's per-column writer slot). Owned Arrow buffers are built through
/// <see cref="ExecutionArena.CreateBuffer"/>, which is the arena's conduit into allocator memory.
/// <para>
/// The <c>Acquire</c>/<c>Release</c> pair is the shape the engine's own scratch has (D64): the
/// object survives between scans in its column's pool while the memory behind it belongs to one
/// execution's arena. A released writer holds no arena and no array, so it cannot be used by
/// accident — <see cref="Arena"/> throws rather than handing out a stale one.
/// </para>
/// </remarks>
internal abstract class PocoChunkWriter<T>
{
    private byte[] _bits = [];
    private ExecutionArena? _arena;

    protected PocoChunkWriter(ChalkType type) => Type = type;

    /// <summary>The largest batch this scratch can hold.</summary>
    public int Capacity { get; private set; }

    /// <summary>The logical type this column emits, which a view has to carry.</summary>
    public ChalkType Type { get; }

    /// <summary>Where the staging came from, and where the batch's buffers come from.</summary>
    protected ExecutionArena Arena => _arena
                                      ?? throw new InvalidOperationException(
                                          "this chunk writer is not attached to an execution; Acquire must be called before it writes.");

    /// <summary>One byte per row, or null when the column cannot hold a NULL.</summary>
    protected byte[]? Valid { get; private set; }

    /// <summary>Points a pooled writer at one scan's arena and batch size, and rents its staging.</summary>
    public void Acquire(ExecutionArena arena, int capacity)
    {
        ArgumentNullException.ThrowIfNull(arena);
        _arena = arena;
        Capacity = capacity;
        Valid = Type.Nullable ? arena.Rent<byte>(capacity) : null;
        _bits = Type.Nullable ? arena.Rent<byte>(BitmapBytes(capacity)) : [];
        AcquireCore(arena, capacity);
    }

    /// <summary>
    /// Returns every rented array and detaches from the arena. Safe the moment a scan ends: the
    /// batches own their buffers. Calling it twice is a no-op.
    /// </summary>
    public void Release()
    {
        if (_arena is not { } arena)
        {
            return;
        }

        if (Valid is { } valid)
        {
            arena.Return(valid);
            Valid = null;
        }

        arena.Return(_bits);
        _bits = [];
        ReleaseCore();
        _arena = null;
        Capacity = 0;
    }

    /// <summary>Encodes <paramref name="count"/> consecutive rows from <paramref name="start"/>.</summary>
    public IArrowArray Write(IReadOnlyList<T> rows, int start, int count)
    {
        Fill(rows, positions: null, start, count);
        return Build(count);
    }

    /// <summary>
    /// Encodes the rows at <c>positions[start .. start + count)</c> — the index-lookup gather (M2
    /// §5). This is the row-access-by-index M1 kept for exactly this purpose: no rows are
    /// materialised, the compiled loop reads them straight out of the collection.
    /// </summary>
    public IArrowArray Write(IReadOnlyList<T> rows, int[] positions, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(positions);
        Fill(rows, positions, start, count);
        return Build(count);
    }

    /// <summary>
    /// The columnar fast path of <c>15-zero-allocation-execution.md</c> §1 (D61): the same chunk loop,
    /// and then a <see cref="ColumnView"/> straight over the staging arrays. No copy into allocator
    /// memory, and no Arrow object at all — the view is valid until the next batch, which is the
    /// contract every operator already has.
    /// </summary>
    public ColumnView WriteView(IReadOnlyList<T> rows, int start, int count)
    {
        Fill(rows, positions: null, start, count);
        return BuildView(count);
    }

    /// <summary>The gather form of <see cref="WriteView(IReadOnlyList{T}, int, int)"/>.</summary>
    public ColumnView WriteView(IReadOnlyList<T> rows, int[] positions, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(positions);
        Fill(rows, positions, start, count);
        return BuildView(count);
    }

    /// <summary>Runs the chunk loop into the staging arrays; <paramref name="positions"/> null = consecutive.</summary>
    protected abstract void Fill(IReadOnlyList<T> rows, int[]? positions, int start, int count);

    /// <summary>Packs the staging into the Arrow array for this batch.</summary>
    protected abstract IArrowArray Build(int count);

    /// <summary>The staging as a view, for the columnar path. Nothing is copied.</summary>
    protected abstract ColumnView BuildView(int count);

    /// <summary>Rents what this writer stages on top of the validity staging.</summary>
    protected abstract void AcquireCore(ExecutionArena arena, int capacity);

    /// <summary>Hands back what the concrete writer rented on top of the validity staging.</summary>
    protected abstract void ReleaseCore();

    /// <summary>Rounds a row count up to the bytes its validity bitmap needs.</summary>
    protected static int BitmapBytes(int rows) => (rows + 7) / 8;

    /// <summary>
    /// Packs the byte-per-row validity into Arrow's bitmap. Arrow wants an empty buffer, not a
    /// bitmap of all-ones, when nothing is null.
    /// </summary>
    protected (ArrowBuffer Buffer, int NullCount) BuildValidity(int count)
    {
        if (Valid is null)
        {
            return (ArrowBuffer.Empty, 0);
        }

        var bytes = BitmapBytes(count);
        if (_bits.Length < bytes)
        {
            var grown = Arena.Rent<byte>(bytes);
            Arena.Return(_bits);
            _bits = grown;
        }

        var bits = _bits.AsSpan(0, bytes);
        bits.Clear();

        var nulls = 0;
        for (var i = 0; i < count; i++)
        {
            if (Valid[i] != 0)
            {
                BitUtility.SetBit(bits, i);
            }
            else
            {
                nulls++;
            }
        }

        return nulls == 0 ? (ArrowBuffer.Empty, 0) : (Arena.CreateBuffer(bits), nulls);
    }

    /// <summary>
    /// The same bitmap as <see cref="BuildValidity"/>, left where it is: a view borrows the staging
    /// rather than copying it into allocator memory.
    /// </summary>
    protected (ReadOnlyMemory<byte> Bits, int NullCount) BuildValidityView(int count)
    {
        if (Valid is null)
        {
            return (default, 0);
        }

        var bytes = BitmapBytes(count);
        if (_bits.Length < bytes)
        {
            var grown = Arena.Rent<byte>(bytes);
            Arena.Return(_bits);
            _bits = grown;
        }

        var bits = _bits.AsSpan(0, bytes);
        bits.Clear();

        var nulls = 0;
        for (var i = 0; i < count; i++)
        {
            if (Valid[i] != 0)
            {
                BitUtility.SetBit(bits, i);
            }
            else
            {
                nulls++;
            }
        }

        return nulls == 0 ? (default, 0) : (_bits.AsMemory(0, bytes), nulls);
    }

    /// <summary>Grows a rented staging array, keeping <paramref name="keep"/> bytes of what is in it.</summary>
    protected byte[] Grow(byte[] staging, int required, int keep)
    {
        if (staging.Length >= required)
        {
            return staging;
        }

        var grown = Arena.Rent<byte>(Math.Max(required, staging.Length * 2));
        staging.AsSpan(0, keep).CopyTo(grown);
        Arena.Return(staging);
        return grown;
    }

    /// <summary>
    /// True when <paramref name="view"/>'s validity memory is the bitmap
    /// currently owned by this writer.
    ///
    /// A column with no NULLs has no Arrow validity buffer, so an empty
    /// validity memory is the expected representation.
    /// </summary>
    protected bool IsCurrentValidity(
        in ColumnView view)
    {
        // CanPublish only calls us when NullCount > 0.
        if (view.Length <= 0 ||
            view.Validity.Length != BitmapBytes(view.Length))
        {
            return false;
        }

        return IsBackedBy(
            view.Validity,
            _bits);
    }
    
    private static bool IsBackedBy(
        ReadOnlyMemory<byte> memory,
        byte[] buffer) =>
        MemoryMarshal.TryGetArray(
            memory,
            out ArraySegment<byte> segment)
        && ReferenceEquals(
            segment.Array,
            buffer)
        && segment.Offset == 0
        && segment.Count == memory.Length;

    /// <summary>
    /// Transfers the current Arrow validity bitmap from this writer to
    /// managed output storage.
    ///
    /// The arena relinquishes the byte array rather than returning it to
    /// its pool. The next batch lazily rents another bitmap when required.
    /// </summary>
    protected ArrowBuffer PublishValidityManaged(
        in ColumnView view)
    {
        if (view.NullCount == 0)
        {
            return ArrowBuffer.Empty;
        }

        if (!IsCurrentValidity(view))
        {
            throw new InvalidOperationException(
                "The column view is not backed by this writer's current validity buffer.");
        }

        var byteCount =
            BitmapBytes(view.Length);

        var bits =
            _bits;

        Arena.RelinquishToGc(bits);

        // Ownership has transferred to the Arrow result.
        // The next BuildValidityView() lazily rents a replacement.
        _bits = [];

        return new ArrowBuffer(
            bits.AsMemory(
                0,
                byteCount));
    }
}