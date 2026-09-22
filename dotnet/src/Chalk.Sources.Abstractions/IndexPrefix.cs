using System.Globalization;

namespace Chalk.Sources;

/// <summary>
/// What a <c>LIKE 'p%'</c> pattern means to an index (D282).
/// </summary>
/// <remarks>
/// The planner matches the pattern shape and the client resolves it when it binds the bounds, which
/// is the first moment a parameter's text exists. Both ends of that use these two functions, and so
/// does a source that wants to check its own answers.
/// </remarks>
public static class IndexPrefix
{
    /// <summary>
    /// Whether <paramref name="pattern"/> is <c>p%</c>: one trailing <c>%</c>, and no <c>%</c> or
    /// <c>_</c> anywhere before it. Only such a pattern is a range; anything else is a predicate.
    /// </summary>
    public static bool IsBarePrefix(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern[^1] != '%')
        {
            return false;
        }

        for (var i = 0; i < pattern.Length - 1; i++)
        {
            if (pattern[i] is '%' or '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The text a bare prefix pattern stands for: itself, without the trailing <c>%</c>.</summary>
    public static string Of(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return IsBarePrefix(pattern)
            ? pattern[..^1]
            : throw new ArgumentException(
                $"'{pattern}' is not a bare prefix pattern: a prefix ends in one '%' and holds no "
                + "other wildcard.",
                nameof(pattern));
    }

    /// <summary>
    /// The smallest text above every text that starts with <paramref name="prefix"/>, so that
    /// <c>[prefix, next)</c> is exactly the rows a prefix matches.
    /// </summary>
    /// <remarks>
    /// The last code point incremented, carrying past U+10FFFF and stepping over the surrogate range,
    /// which holds no code points of its own. False when there is no such text — an empty prefix,
    /// which matches everything and therefore has no upper bound at all, and a prefix made only of
    /// U+10FFFF, above which nothing sorts.
    /// </remarks>
    public static bool TryNext(string prefix, out string next)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var end = prefix.Length;
        while (end > 0)
        {
            var start = end - 1;
            var paired = start > 0
                && char.IsLowSurrogate(prefix[start])
                && char.IsHighSurrogate(prefix[start - 1]);

            if (paired)
            {
                start--;
            }

            // An unpaired surrogate is not a code point at all; it is compared as the unit it is.
            var codePoint = paired ? char.ConvertToUtf32(prefix, start) : prefix[start];
            end = start;

            if (codePoint >= 0x10FFFF)
            {
                // Nothing sorts above it, so this position carries and the one before it increments.
                continue;
            }

            var incremented = codePoint + 1;
            if (incremented == 0xD800)
            {
                // The surrogate range encodes no code point of its own.
                incremented = 0xE000;
            }

            next = string.Concat(
                prefix.AsSpan(0, start),
                (incremented > 0xFFFF
                    ? char.ConvertFromUtf32(incremented)
                    : ((char)incremented).ToString()).AsSpan());
            return true;
        }

        next = string.Empty;
        return false;
    }

    /// <summary>
    /// A bound value as the text a prefix is made of. A STRING bound arrives as a CLR string or as a
    /// <see cref="Utf8String"/>, depending on how the plan carried it.
    /// </summary>
    public static string? AsText(object? value) => value switch
    {
        null => null,
        string text => text,
        Utf8String utf8 => utf8.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
