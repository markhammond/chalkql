using System.Buffers;
using Apache.Arrow.Memory;

namespace Chalk.Execution.Memory;

/// <summary>
/// Arrow buffers backed by ordinary GC arrays, for output batches under
/// <c>OutputMemory.Managed</c> (<c>docs/design/04-client.md</c> §6.1) — the default, because a host
/// that keeps a batch beyond the enumeration must not be holding pooled memory.
/// </summary>
/// <remarks>
/// <c>Dispose</c> is deliberately a no-op: the collector owns the array, so disposing an output batch
/// is harmless and not disposing it is harmless too. That is the whole promise of "managed".
/// </remarks>
internal sealed class ManagedMemoryAllocator : MemoryAllocator
{
    /// <summary>One instance is enough; it holds no state.</summary>
    public static ManagedMemoryAllocator Instance { get; } = new();

    private ManagedMemoryAllocator()
        : base(alignment: 64)
    {
    }

    protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
    {
        bytesAllocated = length;
        return new ManagedBuffer(new byte[length]);
    }

    private sealed class ManagedBuffer : IMemoryOwner<byte>
    {
        public ManagedBuffer(byte[] array) => Memory = array;

        public Memory<byte> Memory { get; }

        public void Dispose()
        {
        }
    }
}
