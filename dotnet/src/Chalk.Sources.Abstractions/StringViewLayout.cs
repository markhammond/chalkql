using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chalk.Sources;

internal static class StringViewLayout
{
    public const int Width = 16;
    public const int InlineByteCount = 12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Length(
        ReadOnlySpan<byte> lane) =>
        MemoryMarshal.Read<int>(lane);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInline(
        ReadOnlySpan<byte> lane) =>
        Length(lane) <= InlineByteCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BufferIndex(
        ReadOnlySpan<byte> lane) =>
        MemoryMarshal.Read<int>(
            lane.Slice(8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BufferOffset(
        ReadOnlySpan<byte> lane) =>
        MemoryMarshal.Read<int>(
            lane.Slice(12));
}