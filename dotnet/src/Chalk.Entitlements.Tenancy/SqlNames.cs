using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// The names the compiler writes into the SQL of a descriptor: a column's, or a table's as its
/// qualifier. A name is written as it is where the parser reads it as that name, and quoted where it
/// would not, so a host never renames a column to suit SQL.
/// </summary>
/// <remarks>
/// A name the parser reads as itself is a plain identifier that SQL does not reserve: <c>At</c>,
/// <c>Open</c> and <c>Value</c> are reserved, and so is <c>user</c>, which unquoted is SQL's
/// <c>USER</c> and never the column. Names are matched without regard to case, so a quoted name in
/// the catalog's spelling reads the same column an unquoted one would. Writing only the names that
/// need it keeps every other descriptor's text, and so its hash, as it was.
/// </remarks>
internal static partial class SqlNames
{
    private static readonly FrozenSet<string> Reserved = Load();

    /// <summary><paramref name="name"/> as the descriptor's SQL writes it.</summary>
    public static string Of(string name) =>
        Plain().IsMatch(name) && !Reserved.Contains(name)
            ? name
            : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Whether SQL reserves <paramref name="word"/>, matched without regard to case.</summary>
    public static bool IsReserved(string word) => Reserved.Contains(word);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Plain();

    private static FrozenSet<string> Load()
    {
        using var stream = typeof(SqlNames).Assembly.GetManifestResourceStream("Chalk.Entitlements.Tenancy.ReservedWords.txt")
            ?? throw new InvalidOperationException("the reserved-word list is not embedded in this assembly");
        using var reader = new StreamReader(stream);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                words.Add(line.Trim());
            }
        }

        return words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
