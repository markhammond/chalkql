using System.Text;

namespace Chalk.Sources.Poco;

/// <summary>
/// How a CLR member name becomes a SQL column name. Unquoted identifiers keep their case and match
/// case-insensitively (D15), so <see cref="AsIs"/> is friendly for <c>SELECT symbol FROM bars</c>
/// against a <c>Symbol</c> property; <see cref="SnakeCase"/> exists for hosts whose SQL is written
/// that way.
/// </summary>
public enum PocoNamingPolicy
{
    /// <summary>The member name, verbatim. The default.</summary>
    AsIs = 0,

    /// <summary><c>SymbolId</c> becomes <c>symbol_id</c>, <c>HTTPStatus</c> becomes <c>http_status</c>.</summary>
    SnakeCase = 1,
}

/// <summary>Applies a <see cref="PocoNamingPolicy"/>. An explicit name always wins over the policy.</summary>
internal static class PocoNaming
{
    public static string Apply(string member, PocoNamingPolicy policy) => policy switch
    {
        PocoNamingPolicy.SnakeCase => ToSnakeCase(member),
        _ => member,
    };

    /// <summary>
    /// Splits on the boundaries a reader would: a lower-to-upper transition, a digit-to-upper
    /// transition, and the last capital of a run that is followed by a lowercase letter (so
    /// <c>HTTPStatus</c> splits once, not four times).
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && NeedsSeparator(name, i))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool NeedsSeparator(string name, int i)
    {
        var previous = name[i - 1];
        if (!char.IsUpper(previous))
        {
            return previous != '_';
        }

        return i + 1 < name.Length && char.IsLower(name[i + 1]);
    }
}
