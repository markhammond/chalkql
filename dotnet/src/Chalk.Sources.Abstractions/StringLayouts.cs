namespace Chalk.Sources;

/// <summary>
/// The Arrow layouts a STRING column may be carried in (D244). A set, because a host may accept
/// more than one and leave the choice to the engine.
/// </summary>
/// <remarks>
/// <para>
/// One flag per physical layout Arrow defines for UTF-8. <see cref="Utf8"/> is the classic
/// <c>StringArray</c> — 32-bit offsets and one data buffer; <see cref="Utf8View"/> is
/// <c>StringViewArray</c> — 16-byte views, a value of twelve bytes or fewer inlined and anything
/// longer carried as prefix, variadic buffer index and offset. They are different Arrow types
/// (<c>utf8</c> and <c>utf8view</c>), not two spellings of one, which is why a prepared query
/// declares which one it emits rather than leaving a host to test for both.
/// </para>
/// <para>
/// BINARY has the same pair in Arrow and keeps the classic layout here; a <c>binaryview</c> sibling
/// is a decision of its own and not part of D244.
/// </para>
/// </remarks>
[Flags]
public enum StringLayouts
{
    /// <summary>The classic <c>StringArray</c>: 32-bit offsets and one data buffer.</summary>
    Utf8 = 1,

    /// <summary>
    /// <c>StringViewArray</c>: 16-byte views, twelve bytes or fewer inline, otherwise prefix,
    /// buffer index and offset. The executor's own representation.
    /// </summary>
    Utf8View = 2,

    /// <summary>Either will do, and the engine declares the one that spares work, per column.</summary>
    Any = Utf8 | Utf8View,
}
