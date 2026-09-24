using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.Unicode;

namespace Chalk;

/// <summary>
/// The UTF-8 against UTF-16 arithmetic that <see cref="Utf8String"/>, the span extensions and
/// <see cref="Utf8StringComparer"/> share: equality between a run of UTF-8 bytes and a
/// <see cref="string"/>, and the hash of a <see cref="string"/> taken over the UTF-8 it would encode
/// to — both without allocating, so a batch's bytes can meet a host's strings in a dictionary.
/// </summary>
internal static class Utf8Ordinal
{
    private const int StackLimit = 256;

    /// <summary>
    /// Whether <paramref name="utf8"/> spells <paramref name="text"/>. A null string equals nothing;
    /// a string that is not valid UTF-16 — a lone surrogate — equals nothing either, because no UTF-8
    /// encodes it.
    /// </summary>
    public static bool Equals(ReadOnlySpan<byte> utf8, string? text)
    {
        if (text is null)
        {
            return false;
        }

        // For valid Unicode the UTF-8 byte count is at least the UTF-16 code unit count, so equal
        // lengths can only match when every character is ASCII — the fast path for symbols and
        // identifiers. Each byte is required to be that ASCII character, which also keeps a byte
        // outside ASCII from matching a code unit of the same value.
        if (utf8.Length == text.Length)
        {
            for (var i = 0; i < utf8.Length; i++)
            {
                var c = text[i];
                if (c >= 0x80 || utf8[i] != c)
                {
                    return false;
                }
            }

            return true;
        }

        if (utf8.Length < text.Length)
        {
            return false;
        }

        byte[]? rented = null;
        var encoded = utf8.Length <= StackLimit
            ? stackalloc byte[utf8.Length]
            : (rented = ArrayPool<byte>.Shared.Rent(utf8.Length)).AsSpan(0, utf8.Length);
        try
        {
            var status = Utf8.FromUtf16(
                text.AsSpan(),
                encoded,
                out _,
                out var written,
                replaceInvalidSequences: false,
                isFinalBlock: true);
            return status == OperationStatus.Done
                && written == utf8.Length
                && encoded[..written].SequenceEqual(utf8);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// <see cref="Utf8String.Hash"/> of the UTF-8 <paramref name="text"/> encodes to, computed
    /// without allocating: the hash a <see cref="Utf8String"/> made from the same text has, which is
    /// what lets a <c>string</c> key and a batch's bytes meet in one dictionary. A string that is not
    /// valid UTF-16 hashes its code units instead; it can equal no UTF-8 value anyway, so the value
    /// only has to be deterministic.
    /// </summary>
    public static int Hash(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return Utf8String.Hash(default);
        }

        // Three bytes per code unit is the most UTF-8 needs: a surrogate pair is two code units and
        // four bytes.
        var most = checked(text.Length * 3);
        byte[]? rented = null;
        var encoded = most <= StackLimit
            ? stackalloc byte[most]
            : (rented = ArrayPool<byte>.Shared.Rent(most)).AsSpan(0, most);
        try
        {
            var status = Utf8.FromUtf16(
                text,
                encoded,
                out _,
                out var written,
                replaceInvalidSequences: false,
                isFinalBlock: true);
            return status == OperationStatus.Done
                ? Utf8String.Hash(encoded[..written])
                : Utf8String.Hash(MemoryMarshal.AsBytes(text));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
