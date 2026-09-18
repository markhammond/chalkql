using System.Text;
using Chalk.Catalog;

namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// A mask written once for a whole realm and instantiated per column
/// (<c>docs/design/16-entitlements.md</c> §5, D219).
/// </summary>
/// <remarks>
/// <para>
/// The template is DuckDB's lambda form — <c>lambda v: SUBSTRING(v, 1, 1)</c> — which is the
/// canonical spelling here because a host that writes SQL for a mask already knows it. The compiler
/// substitutes the parameter with the column's name <em>token-wise</em>: an identifier token equal
/// to the parameter is the column, and text inside a string literal or a quoted identifier is text.
/// So one rule covers every column of a realm, and one template is reused across columns and tables.
/// </para>
/// <para>
/// A mask without the <c>lambda</c> prefix is taken verbatim, which is what a mask naming one column
/// has always been. What is <b>not</b> accepted any more is the <c>{column}</c> token this package
/// used for the same purpose: it was a substitution into text rather than into SQL, so it read the
/// inside of a string literal as a place to substitute, and the refusal names the lambda form.
/// </para>
/// <para>
/// Layer A is unchanged either way. The wire carries the instantiated text, so nothing in the core
/// knows a template existed (D143).
/// </para>
/// </remarks>
internal static class MaskTemplate
{
    private const string Keyword = "lambda";

    /// <summary>Whether this mask is a template rather than an expression to take verbatim.</summary>
    internal static bool IsTemplate(string mask)
    {
        var text = mask.AsSpan().TrimStart();
        return text.Length > Keyword.Length
            && text[..Keyword.Length].Equals(Keyword, StringComparison.OrdinalIgnoreCase)
            && (char.IsWhiteSpace(text[Keyword.Length]) || text[Keyword.Length] == '(');
    }

    /// <summary>
    /// This mask as it reads for <paramref name="column"/>: the template instantiated, or the text
    /// itself where it is not one.
    /// </summary>
    /// <param name="mask">The mask the rule declared.</param>
    /// <param name="column">The column being masked.</param>
    /// <param name="table">The table it belongs to, for the shadowing check.</param>
    /// <param name="path">Where a refusal says it was declared.</param>
    /// <exception cref="CatalogValidationException">
    /// The mask still uses the retired <c>{column}</c> token, or the template takes other than one
    /// parameter, or its parameter shadows a column of the table.
    /// </exception>
    internal static string Instantiate(
        string mask, string column, TableDescriptor table, string path)
    {
        Check(mask, table, path);
        if (!IsTemplate(mask))
        {
            return mask;
        }

        var (parameter, body) = Split(mask);
        return Substitute(body, parameter, column);
    }

    /// <summary>
    /// Everything about a declared mask that does not depend on which column it is instantiated for,
    /// so that a template the winning rule never reaches is refused too.
    /// </summary>
    internal static void Check(string mask, TableDescriptor table, string path)
    {
        if (mask.Contains("{column}", StringComparison.Ordinal))
        {
            throw new CatalogValidationException(
                path,
                $"the mask '{mask}' uses the retired '{{column}}' token. A mask that covers more "
                + "than one column is a template in lambda notation — "
                + "\"lambda v: SUBSTRING(v, 1, 1)\" — which substitutes an identifier token and "
                + "never text inside a string literal (docs/design/16-entitlements.md §5, D219).");
        }

        if (!IsTemplate(mask))
        {
            return;
        }

        var (parameter, body) = Split(mask, path);
        if (body.Length == 0)
        {
            throw new CatalogValidationException(
                path, $"the mask template '{mask}' has no body after its ':'.");
        }

        if (TenancyCompiler.Column(table, parameter) is { } shadowed)
        {
            throw new CatalogValidationException(
                path,
                $"the mask template '{mask}' names its parameter '{parameter}', which is also a "
                + $"column of '{table.Name}' ('{shadowed.Name}'). Every identifier token equal to "
                + "the parameter becomes the masked column's name, so the template could not read "
                + "that column at all: name the parameter something the table does not carry.");
        }
    }

    /// <summary>The parameter and the body of a template, refusing anything but one parameter.</summary>
    private static (string Parameter, string Body) Split(string mask, string path = "")
    {
        var text = mask.TrimStart();
        var after = text[Keyword.Length..];
        var colon = after.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            throw new CatalogValidationException(
                path,
                $"the mask template '{mask}' has no ':' between its parameter and its body. The "
                + "form is \"lambda v: SUBSTRING(v, 1, 1)\" (D219).");
        }

        var parameters = after[..colon].Trim();
        if (parameters.StartsWith('(') && parameters.EndsWith(')'))
        {
            parameters = parameters[1..^1].Trim();
        }

        var names = parameters.Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length != 1 || !IsIdentifier(names[0]))
        {
            throw new CatalogValidationException(
                path,
                $"the mask template '{mask}' takes {names.Length} parameters and a mask template "
                + "takes exactly one: it stands for the column being masked and there is nothing "
                + "else for a second to stand for (D219).");
        }

        return (names[0], after[(colon + 1)..].Trim());
    }

    /// <summary>
    /// The body with every identifier token equal to <paramref name="parameter"/> replaced by
    /// <paramref name="column"/>.
    /// </summary>
    /// <remarks>
    /// A string literal and a quoted identifier are copied through whole, doubled quotes included,
    /// so <c>lambda v: COALESCE(v, 'v')</c> masks with the literal <c>'v'</c> and not with the
    /// column's name. An identifier immediately after a <c>.</c> is a qualified part —
    /// <c>@ctx.mask_key</c>'s second half — and is never the parameter.
    /// </remarks>
    private static string Substitute(string body, string parameter, string column)
    {
        var text = new StringBuilder(body.Length + column.Length);
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c is '\'' or '"')
            {
                i = Quoted(body, i, text);
                continue;
            }

            if (IsStart(c))
            {
                var start = i;
                while (i < body.Length && IsPart(body[i]))
                {
                    i++;
                }

                var token = body[start..i];
                var qualified = start > 0 && body[start - 1] == '.';
                text.Append(
                    !qualified && string.Equals(token, parameter, StringComparison.OrdinalIgnoreCase)
                        ? column
                        : token);
                continue;
            }

            text.Append(c);
            i++;
        }

        return text.ToString();
    }

    /// <summary>Copies one quoted run through, ending after its closing quote.</summary>
    private static int Quoted(string body, int at, StringBuilder text)
    {
        var quote = body[at];
        text.Append(quote);
        var i = at + 1;
        while (i < body.Length)
        {
            text.Append(body[i]);
            if (body[i] != quote)
            {
                i++;
                continue;
            }

            i++;
            if (i < body.Length && body[i] == quote)
            {
                // A doubled quote is one character of the value, not the end of the run.
                text.Append(body[i]);
                i++;
                continue;
            }

            return i;
        }

        return i;
    }

    /// <summary>
    /// The identifier tokens of an expression: the names it could be reading, and nothing that is
    /// text, a qualified part or a function name (D220).
    /// </summary>
    /// <remarks>
    /// This is a token scan and not a parser — the package has no SQL parser and §2 forbids it
    /// growing one — so it is deliberately generous about what an identifier is: every keyword comes
    /// back too. The caller only asks whether a token <em>is a column of this table</em>, and a
    /// keyword is not. The authoritative check is the sidecar's, over the converted expression.
    /// </remarks>
    internal static IEnumerable<string> Identifiers(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                i = Skip(sql, i);
                continue;
            }

            if (c == '"')
            {
                var quoted = new StringBuilder();
                var end = Quoted(sql, i, quoted);
                var name = quoted.ToString();
                i = end;
                if (name.Length >= 2)
                {
                    yield return name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                }

                continue;
            }

            if (!IsStart(c))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < sql.Length && IsPart(sql[i]))
            {
                i++;
            }

            // A qualified part — `@ctx.mask_key`'s second half — names nothing of this table, and a
            // name followed by '(' is the function being called.
            var qualified = start > 0 && sql[start - 1] == '.';
            var called = i < sql.Length && sql[i] == '(';
            if (!qualified && !called)
            {
                yield return sql[start..i];
            }
        }
    }

    /// <summary>Past one quoted run, copying nothing: the caller wants the text skipped.</summary>
    private static int Skip(string sql, int at) => Quoted(sql, at, new StringBuilder());

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !IsStart(text[0]))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!IsPart(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
