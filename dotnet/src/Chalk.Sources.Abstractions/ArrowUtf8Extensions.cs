using System.Runtime.InteropServices;
using Apache.Arrow;

namespace Chalk;

/// <summary>
/// Reading a result batch's text without making a string (D147,
/// <c>docs/design/24-zero-gc.md</c> §5).
/// </summary>
/// <remarks>
/// A host that consumes <c>QueryAsync</c> receives Arrow record batches, and a STRING column arrives
/// as a <see cref="StringArray"/> over a UTF-8 buffer. Arrow's own accessor is
/// <c>GetString(int)</c>, which decodes; these give the marker type instead, so the host decides
/// when — and whether — a string is made.
/// </remarks>
public static class ArrowUtf8Extensions
{
    /// <summary>
    /// One row of a STRING column as a <see cref="Utf8String"/>, or the default value when the row
    /// is NULL (which a caller distinguishes with <c>IsNull(index)</c>).
    /// </summary>
    /// <remarks>
    /// The value borrows the array's buffer: it is valid as long as that record batch is, and a host
    /// that keeps it past the batch's disposal calls <c>ToArray()</c> or <c>ToString()</c>. The bytes
    /// are valid UTF-8 by construction and are not checked again here.
    /// </remarks>
    public static Utf8String GetUtf8(this StringArray array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.IsNull(index))
        {
            return default;
        }

        var offsets = array.ValueOffsets;
        var start = offsets[index];
        return new Utf8String(array.ValueBuffer.Memory.Slice(start, offsets[index + 1] - start));
    }

    /// <summary>
    /// One row of a STRING column carried as Arrow's view layout, which is what a STRING column is
    /// by default since D244. The same contract as the classic overload: the value borrows the
    /// array's buffers, and a host that keeps it past the batch calls <c>ToArray()</c> or
    /// <c>ToString()</c>.
    /// </summary>
    /// <remarks>
    /// A value of twelve bytes or fewer lives in its own sixteen-byte lane; a longer one lives in
    /// the variadic buffer its lane names, and neither is copied here.
    /// </remarks>
    public static Utf8String GetUtf8(this StringViewArray array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.IsNull(index))
        {
            return default;
        }

        const int Width = 16;
        const int InlineLength = 12;

        var lane = array.Data.Buffers[1].Memory.Slice(
            checked((array.Offset + index) * Width), Width);

        var length = MemoryMarshal.Read<int>(lane.Span);
        if ((uint)length <= InlineLength)
        {
            return new Utf8String(lane.Slice(sizeof(int), length));
        }

        // [length:4][prefix:4][buffer index:4][buffer offset:4]; variadic buffer zero is Buffers[2].
        var buffer = MemoryMarshal.Read<int>(lane.Span.Slice(8));
        var offset = MemoryMarshal.Read<int>(lane.Span.Slice(12));
        return new Utf8String(array.Data.Buffers[2 + buffer].Memory.Slice(offset, length));
    }
}
