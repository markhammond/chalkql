using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// One grouping key as two words and a length (D255): the shape the single-key hash aggregate hashes,
/// compares and validates a perfect-hash slot with, in place of a byte-at-a-time hash and a
/// <c>SequenceEqual</c> over a lane read per row.
/// </summary>
/// <remarks>
/// <para>
/// A fixed-width key of up to sixteen bytes is its own image: one word, or two for DECIMAL and UUID,
/// with <see cref="Length"/> the layout's width. A variable-length key of up to sixteen bytes packs
/// its bytes into the same two words — the eight-and-over cases overlap their two reads rather than
/// padding, which is why the packing is only injective <em>for one length</em> and why the length is
/// part of the image and is compared first.
/// </para>
/// <para>
/// A longer key images its first sixteen bytes, and equal images are then not equal keys: such a
/// group is marked by a <see cref="Length"/> above <see cref="MaxInline"/> and its equality falls
/// back to the key store's full compare. A NULL key is <see cref="NullLength"/> with both words zero,
/// so it equals another NULL and nothing else (<c>02-ir.md</c> §4).
/// </para>
/// </remarks>
internal readonly struct KeyImage
{
    /// <summary>The longest key the two words hold losslessly.</summary>
    public const int MaxInline = 16;

    /// <summary>The <see cref="Length"/> of a NULL key, which no real length can take.</summary>
    public const int NullLength = -1;

    public KeyImage(ulong low, ulong high, int length)
    {
        Low = low;
        High = high;
        Length = length;
    }

    public ulong Low { get; }

    public ulong High { get; }

    /// <summary>The key's byte count, or <see cref="NullLength"/> for a NULL key.</summary>
    public int Length { get; }

    public bool IsNull => Length == NullLength;

    /// <summary>A NULL key, which groups with every other NULL.</summary>
    public static KeyImage Null => new(0, 0, NullLength);

    /// <summary>The hash the probe table uses. NULL keeps the salt it has always had.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Hash() => IsNull
        ? Numeric.Hashing.NullSalt
        : Numeric.Hashing.Words(Low, High, Length);

    /// <summary>
    /// A fixed-width lane, whose width is the image's length. Widths of one, two, four and eight
    /// bytes are one word; sixteen — DECIMAL and UUID — are two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyImage OfFixed(ReadOnlySpan<byte> lane, int width) => width switch
    {
        8 => new KeyImage(MemoryMarshal.Read<ulong>(lane), 0, 8),
        4 => new KeyImage(MemoryMarshal.Read<uint>(lane), 0, 4),
        16 => new KeyImage(
            MemoryMarshal.Read<ulong>(lane), MemoryMarshal.Read<ulong>(lane[8..]), 16),
        2 => new KeyImage(MemoryMarshal.Read<ushort>(lane), 0, 2),
        1 => new KeyImage(lane[0], 0, 1),
        _ => OfBytes(lane[..width]),
    };

    /// <summary>
    /// A variable-length key's bytes. Up to sixteen of them are packed losslessly for that length;
    /// beyond that the image is the first sixteen and <see cref="IsExact"/> is false.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static KeyImage OfBytes(ReadOnlySpan<byte> value)
    {
        var length = value.Length;
        var taken = length < MaxInline ? length : MaxInline;
        ulong low;
        ulong high = 0;

        if (taken >= 8)
        {
            low = MemoryMarshal.Read<ulong>(value);
            high = MemoryMarshal.Read<ulong>(value[(taken - 8)..]);
        }
        else if (taken >= 4)
        {
            low = MemoryMarshal.Read<uint>(value)
                | ((ulong)MemoryMarshal.Read<uint>(value[(taken - 4)..]) << 32);
        }
        else if (taken > 0)
        {
            // The three-byte spread wyhash uses for its shortest inputs: bytes 0, the middle and the
            // last cover every byte of a one-, two- or three-byte value without reading past it.
            low = value[0]
                | ((ulong)value[taken >> 1] << 16)
                | ((ulong)value[taken - 1] << 24);
        }
        else
        {
            low = 0;
        }

        return new KeyImage(low, high, length);
    }
}
