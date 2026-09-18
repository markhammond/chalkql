using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Chalk.Execution.Vectors;

/// <summary>
/// Turning a classic Arrow <see cref="StringArray"/> into a <see cref="StringViewArray"/> without
/// moving a payload byte (D244).
/// </summary>
/// <remarks>
/// The classic data buffer becomes variadic buffer zero; a value of twelve bytes or fewer is copied
/// into its own sixteen-byte view, and a longer one carries its four-byte prefix, buffer index zero
/// and its offset into that same buffer. The validity bitmap is shared. What is built is the view
/// lane array and nothing else — one buffer of <c>16 × rows</c>, per batch and never per row.
/// </remarks>
internal static class StringViewArrays
{
    private const int Width = 16;
    private const int InlineLength = 12;

    /// <summary>
    /// <paramref name="classic"/>'s rows as a <see cref="StringViewArray"/> over its own buffers.
    /// The array must start at row zero, which is what the output boundary hands over.
    /// </summary>
    public static StringViewArray OverClassic(StringArray classic)
    {
        ArgumentNullException.ThrowIfNull(classic);
        if (classic.Offset != 0)
        {
            throw new InvalidOperationException(
                "a sliced StringArray cannot be re-described as views without rebasing its offsets.");
        }

        var rows = classic.Length;
        var data = classic.Data.Buffers[2];

        var lanes = new byte[checked(rows * Width)];
        var descriptors = MemoryMarshal.Cast<byte, int>(lanes.AsSpan());
        var offsets = MemoryMarshal.Cast<byte, int>(classic.Data.Buffers[1].Span);
        var payload = data.Span;

        var external = false;
        for (var row = 0; row < rows; row++)
        {
            var start = offsets[row];
            var length = offsets[row + 1] - start;
            var lane = row * 4;
            descriptors[lane] = length;

            if (length <= InlineLength)
            {
                if (length != 0)
                {
                    payload
                        .Slice(start, length)
                        .CopyTo(lanes.AsSpan((row * Width) + sizeof(int), length));
                }

                continue;
            }

            // [length:4][prefix:4][buffer index:4][buffer offset:4]; the index stays zero, which the
            // freshly allocated lane array already is.
            descriptors[lane + 1] = MemoryMarshal.Read<int>(payload.Slice(start, sizeof(int)));
            descriptors[lane + 3] = start;
            external = true;
        }

        var buffers = external
            ? new[] { classic.Data.Buffers[0], new ArrowBuffer(lanes), data }
            : [classic.Data.Buffers[0], new ArrowBuffer(lanes)];

        return new StringViewArray(
            new ArrayData(
                StringViewType.Default,
                rows,
                classic.NullCount,
                0,
                buffers,
                children: null));
    }
}
