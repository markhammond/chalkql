namespace Chalk.Catalog;

/// <summary>
/// The shape of a Calcite constant name — <c>LENIENT</c>, <c>STRICT_2003</c>, <c>BIG_QUERY</c> — which
/// is how <see cref="SqlConformance"/> and <see cref="SqlLibrary"/> travel (D318). Whether the
/// sidecar <em>knows</em> a name is the sidecar's to say; this refuses only what cannot be one.
/// </summary>
internal static class CalciteNames
{
    public static string Check(string name, string kind, string examples, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, paramName);
        if (!char.IsAsciiLetterUpper(name[0]) || !name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"'{name}' is not a Calcite {kind} name, which is spelled as Calcite spells its constant — "
                + $"upper-case letters, digits and underscores, as {examples}.",
                paramName);
        }

        return name;
    }
}
