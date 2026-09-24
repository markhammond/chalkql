using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Chalk.Catalog;

namespace Chalk.Sources;

/// <summary>
/// One column of one batch, in Arrow's physical layout and with no Arrow object anywhere
/// (<c>docs/design/15-zero-allocation-execution.md</c> §1, D61).
/// </summary>
/// <remarks>
/// <para>
/// A view borrows memory it does not own — an operator's arena-rented output buffers, a source's
/// Arrow buffers, an expression node's scratch — and is valid exactly as long as its producer says:
/// until that producer's next batch. Anything that must outlive a batch copies, which is what the
/// blocking operators already do.
/// </para>
/// <para>
/// <see cref="Generation"/> is the producing <see cref="ColumnarBatch"/>'s reuse counter. Under
/// <c>ExecutionOptions.ValidateBatchLifetimes</c> (and in Debug builds) every access checks it
/// against the slot's, so a view kept past its batch fails loudly instead of reading the next
/// batch's rows.
/// </para>
/// <para>
/// Public since step 22 under <c>CHALK001</c>, because it is what a Tier 2 kernel reads (D79). It is
/// the engine's own shape rather than a designed API, which is exactly what the experimental
/// attribute is saying.
/// </para>
/// </remarks>
[Experimental("CHALK001")]
public readonly struct ColumnView
{
    internal IManagedColumnPublisher? ManagedPublisher { get; init; }
    
    /// <summary>An empty view of a column that has no rows.</summary>
    public static ColumnView Empty(ChalkType type) => new() { Type = type, Length = 0, NullCount = 0 };

    /// <summary>The logical type. Kernels read the layout from it; semantics come from the IR.</summary>
    public ChalkType Type { get; init; }

    /// <summary>Rows in this column.</summary>
    public int Length { get; init; }

    /// <summary>
    /// The values: fixed-width lanes, or the UTF-8 / opaque data buffer for the variable-length
    /// kinds. Empty for a LIST, whose values live in <see cref="Child"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Values { get; init; }

    /// <summary>Bit-packed validity, or empty when every row holds a value.</summary>
    public ReadOnlyMemory<byte> Validity { get; init; }

    /// <summary>
    /// <c>Length + 1</c> int32 offsets for the variable-length kinds and for LIST; empty otherwise.
    /// </summary>
    public ReadOnlyMemory<byte> Offsets { get; init; }
    
    /// <summary>
    /// STRING_VIEW's variadic payload buffers.
    ///
    /// Null means this column is not stored as STRING_VIEW.
    /// A non-null value identifies STRING_VIEW storage even when
    /// <see cref="ViewBufferCount"/> is zero because every value is inline.
    /// </summary>
    public ReadOnlyMemory<byte>[]? ViewBuffers { get; init; }

    public bool IsUtf8View => ViewBuffers is not null;

    /// <summary>
    /// Active prefix of <see cref="ViewBuffers"/>.
    /// </summary>
    public int ViewBufferCount { get; init; }
    
    /// <summary>Rows whose value is NULL, or -1 when nobody counted.</summary>
    public int NullCount { get; init; }

    /// <summary>
    /// The row this column's buffers start at. Non-zero only for a view over a sliced Arrow array,
    /// where the validity bitmap cannot be sliced without shifting bits.
    /// </summary>
    public int Offset { get; init; }

    /// <summary>
    /// True for a BOOL column that arrived bit-packed from a source or a finished batch. Inside the
    /// pipeline a computed boolean is a byte per row (§6.4), so the two shapes are distinguished
    /// here rather than guessed at from the type.
    /// </summary>
    public bool BitPacked { get; init; }

    /// <summary>
    /// A LIST's element column (D58), a COMPOSITE's field columns in field order (D291), and null for
    /// every other kind. A composite value's fields are aligned with its own rows before <see cref="Offset"/>
    /// applies, as Arrow's are: field <c>i</c> of row <c>r</c> is row <c>Offset + r</c> of
    /// <c>Children[i]</c>.
    /// </summary>
    public ColumnView[]? Children { get; init; }

    /// <summary>The producing slot's reuse counter; 0 for a view over memory nobody reuses.</summary>
    public int Generation { get; init; }

    /// <summary>
    /// The slot this view came from, when lifetimes are being validated. Null otherwise, and
    /// internal: it is the engine's own bookkeeping and a kernel has no use for it.
    /// </summary>
    internal ColumnarBatch? Owner { get; init; }

    /// <summary>The LIST element column. Throws for anything else.</summary>
    public ColumnView Child => Children is { Length: > 0 }
        ? Children[0]
        : throw new InvalidOperationException("This column is not a LIST, or its element view is unset.");

    /// <summary>
    /// Field <paramref name="index"/> of a COMPOSITE column, as a view of this column's rows (D291): the
    /// child view with this view's offset applied, and no validity but the field's own — a field of
    /// a NULL composite is undefined here, and a reader that needs it as NULL intersects the two.
    /// </summary>
    internal ColumnView FieldView(int index) =>
        Children is { } children && index < children.Length
            ? children[index].Slice(Offset, Length)
            : throw new InvalidOperationException(
                $"This column is not a COMPOSITE with a field {index}, or its field views are unset.");

    /// <summary>The values as typed lanes, already advanced past <see cref="Offset"/>.</summary>
    public ReadOnlySpan<T> Lanes<T>()
        where T : unmanaged
    {
        Validate();
        return MemoryMarshal.Cast<byte, T>(Values.Span).Slice(Offset, Length);
    }

    /// <summary>The values as raw bytes for a fixed-width layout of <paramref name="width"/> bytes.</summary>
    public ReadOnlySpan<byte> RawLanes(int width)
    {
        Validate();
        return Values.Span.Slice(Offset * width, Length * width);
    }

    /// <summary>The validity bitmap, or an empty span when every row is valid.</summary>
    public ReadOnlySpan<byte> ValidityBits()
    {
        Validate();
        return Validity.Span;
    }

    /// <summary>The <c>Length + 1</c> offsets of a variable-length or LIST column.</summary>
    public ReadOnlySpan<int> OffsetLanes()
    {
        Validate();
        return MemoryMarshal.Cast<byte, int>(Offsets.Span).Slice(Offset, Length + 1);
    }

    /// <summary>The data buffer of a variable-length column. Index it with <see cref="OffsetLanes"/>.</summary>
    public ReadOnlySpan<byte> VarData()
    {
        Validate();
        return Values.Span;
    }

    /// <summary>The bytes of one row of a variable-length column.</summary>
    /// <summary>
    /// The bytes of one logical variable-length value.
    /// Supports both classic STRING and STRING_VIEW storage.
    /// </summary>
    public ReadOnlySpan<byte> VarValue(int row)
    {
        if (ViewBuffers is not null)
        {
            return Utf8ViewMemory(row).Span;
        }

        var offsets = OffsetLanes();
        var start = offsets[row];

        return Values.Span.Slice(start, offsets[row + 1] - start);
    }

    /// <summary>
    /// The bytes of one row of a variable-length column, as memory rather than a span, so a caller
    /// may hold it across an <c>await</c> (D147).
    /// </summary>
    /// <remarks>
    /// The memory points into this batch's buffers and is valid until the batch is disposed or the
    /// arena reuses it — the lifetime rule of this type. Anything that outlives the batch copies.
    /// </remarks>
    public ReadOnlyMemory<byte> VarValueMemory(int row)
    {
        if (ViewBuffers is not null)
        {
            return Utf8ViewMemory(row);
        }

        var offsets = OffsetLanes();
        var start = offsets[row];

        return Values.Slice(start, offsets[row + 1] - start);
    }
    
    /// <summary>One row of a STRING column as a <see cref="Utf8String"/> (D147).</summary>
    /// <remarks>
    /// The value borrows this batch's buffers and is valid until the batch is disposed or the arena
    /// reuses it. A caller that keeps it calls <c>ToArray()</c> or <c>ToString()</c>. The bytes are
    /// valid UTF-8 by construction — they were checked where they entered the column — and are not
    /// checked again here.
    /// </remarks>
    public Utf8String Utf8(int row) => new(VarValueMemory(row));

    /// <summary>
    /// One row of a STRING_VIEW column as a borrowed UTF-8 value.
    ///
    /// The caller is responsible for using this only with STRING_VIEW physical
    /// storage. For classic STRING storage use <see cref="Utf8"/>.
    /// </summary>
    public Utf8String Utf8View(int row) => new(Utf8ViewMemory(row));
    
    /// <summary>
    /// One row of a STRING_VIEW column as borrowed UTF-8 bytes.
    /// </summary>
    public ReadOnlySpan<byte> Utf8ViewBytes(
        int row) =>
        Utf8ViewMemory(row).Span;
    
    /// <summary>
    /// One row of a STRING_VIEW column as borrowed memory.
    /// </summary>
    public ReadOnlyMemory<byte> Utf8ViewMemory(int row)
    {
        Validate();

        const int width = 16;
        const int inlineLimit = 12;

        var laneOffset =
            checked(
                (Offset + row) *
                width);

        var lane =
            Values.Span.Slice(
                laneOffset,
                width);

        var length =
            MemoryMarshal.Read<int>(
                lane);

        if ((uint)length <= inlineLimit)
        {
            //
            // [length:4][inline payload:12]
            //
            return Values.Slice(
                laneOffset + sizeof(int),
                length);
        }

        //
        // [length:4][prefix:4][buffer index:4][buffer offset:4]
        //
        var bufferIndex =
            MemoryMarshal.Read<int>(
                lane.Slice(8));

        var bufferOffset =
            MemoryMarshal.Read<int>(
                lane.Slice(12));

        var buffers =
            ViewBuffers
            ?? throw new InvalidOperationException(
                "STRING_VIEW references a variadic buffer, "
                + "but the view has no backing buffers.");

        if ((uint)bufferIndex >=
            (uint)ViewBufferCount)
        {
            throw new InvalidOperationException(
                $"STRING_VIEW references backing buffer {bufferIndex}, "
                + $"but only {ViewBufferCount} buffers are present.");
        }

        return buffers[bufferIndex]
            .Slice(
                bufferOffset,
                length);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Utf8ViewLength(int row)
    {
        Validate();

        const int width = 16;

        return MemoryMarshal.Read<int>(
            Values.Span.Slice(
                checked(
                    (Offset + row) *
                    width),
                sizeof(int)));
    }
    
    /// <summary>
    /// STRING_VIEW's 16-byte physical lanes.
    /// </summary>
    public ReadOnlySpan<byte> Utf8ViewLanes()
    {
        Validate();

        return Values.Span.Slice(
            checked(
                Offset *
                StringViewLayout.Width),
            checked(
                Length *
                StringViewLayout.Width));
    }
    
    /// <summary>Whether row <paramref name="row"/> holds a value.</summary>
    public bool IsValid(int row)
    {
        var bits = ValidityBits();
        return bits.IsEmpty || BitUtility.GetBit(bits, Offset + row);
    }

    /// <summary>Reads one row of a BOOL column, packed or not.</summary>
    public bool BoolAt(int row) => BitPacked
        ? BitUtility.GetBit(Values.Span, Offset + row)
        : Values.Span[Offset + row] != 0;

    /// <summary>This view over <paramref name="length"/> rows starting at <paramref name="start"/>.</summary>
    public ColumnView Slice(
        int start,
        int length) =>
        new()
        {
            Type = Type,
            Length = length,

            Values = Values,
            Validity = Validity,
            Offsets = Offsets,

            ViewBuffers = ViewBuffers,
            ViewBufferCount = ViewBufferCount,

            NullCount = -1,
            Offset = Offset + start,

            BitPacked = BitPacked,
            Children = Children,

            Generation = Generation,
            Owner = Owner,

            // Deliberately absent: ManagedPublisher. A slice carries its own offset and an
            // unknown null count, which is exactly what CanPublish refuses, so holding the
            // publisher would promise a zero-copy publish that can never happen.
        };

    /// <summary>
    /// Fails when the producing slot has moved on. The check is off unless the slot asked for it, so
    /// the release path pays one null test per access.
    /// </summary>
    private void Validate()
    {
        if (Owner is { } owner && owner.Generation != Generation)
        {
            throw new BatchLifetimeException(owner.Generation, Generation);
        }
    }
}

internal interface IManagedColumnPublisher
{
    bool CanPublish(in ColumnView view);

    IArrowArray PublishManaged(in ColumnView view);
}

/// <summary>
/// A view was read after the batch that produced it had been reused. Raised only when lifetimes are
/// being validated (<c>ExecutionOptions.ValidateBatchLifetimes</c>, and Debug builds), where it turns
/// a silently wrong answer into a failure that names the two generations
/// (<c>docs/design/15-zero-allocation-execution.md</c> §1).
/// </summary>
public sealed class BatchLifetimeException : ChalkException
{
    internal BatchLifetimeException(int current, int captured)
        : base($"a column view from batch generation {captured} was read after its producer had moved "
            + $"on to generation {current}. A batch is valid until the next MoveNext; copy anything "
            + "that has to outlive it.")
    {
        CurrentGeneration = current;
        CapturedGeneration = captured;
    }

    /// <summary>The producing slot's generation now.</summary>
    public int CurrentGeneration { get; }

    /// <summary>The generation the view was taken at.</summary>
    public int CapturedGeneration { get; }
}
