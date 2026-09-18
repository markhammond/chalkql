using System.Buffers;
using System.Runtime.InteropServices;

namespace Chalk.Sources.Poco;

/// <summary>
/// A <see cref="ReadOnlyMemory{T}"/> of bytes over a typed staging array, so a column view can borrow
/// the chunk loop's own buffer instead of a copy of it
/// (<c>docs/design/15-zero-allocation-execution.md</c> §1).
/// </summary>
/// <remarks>
/// One of these per writer, created once and re-pointed when the staging array is swapped for a
/// larger one, so it costs nothing per batch. <see cref="Pin"/> is refused deliberately: a view is
/// read while its producer is alive and never handed to unmanaged code.
/// </remarks>
internal sealed class PocoByteMemory<TStorage> : MemoryManager<byte>
    where TStorage : unmanaged
{
    private TStorage[] _array = [];

    /// <summary>The first <paramref name="count"/> elements of <paramref name="array"/>, as bytes.</summary>
    public ReadOnlyMemory<byte> Of(TStorage[] array, int count)
    {
        _array = array;
        return Memory[..(count * System.Runtime.CompilerServices.Unsafe.SizeOf<TStorage>())];
    }

    public override Span<byte> GetSpan() => MemoryMarshal.AsBytes(_array.AsSpan());

    public override MemoryHandle Pin(int elementIndex = 0) =>
        throw new NotSupportedException("a column view over POCO staging is never pinned.");

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
