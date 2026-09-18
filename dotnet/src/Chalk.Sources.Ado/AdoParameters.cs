using System.Data.Common;
using System.Globalization;
using System.Text;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Ado;

/// <summary>
/// Rewriting the planner's positional <c>?</c> placeholders into the style a driver takes, naming
/// the parameters to match (§3), and binding values onto a <see cref="DbCommand"/> under those
/// names — the one way an <see cref="IRemoteFetch"/> implementation binds the planner's parameters,
/// in this package or a vendor's (D264).
/// </summary>
/// <remarks>
/// <para>
/// The planner always generates <c>?</c>: that is what Calcite's writer emits for a
/// <c>SqlDynamicParam</c>, and there is no dialect hook to change it. Drivers, however, do not
/// agree — Microsoft.Data.Sqlite and DuckDB.NET both want <c>$name</c>, Npgsql wants <c>@name</c>,
/// and an ODBC-shaped driver wants the <c>?</c> it was given. So the adapter rewrites, which is what
/// <c>DialectProfile.ParameterPlaceholder</c> is for.
/// </para>
/// <para>
/// The rewrite is a scanner, not a regular expression, for the same reason the planner's one-line
/// normaliser is: a <c>?</c> inside a string literal or a quoted identifier is data, and replacing
/// it would change the query's meaning. Literals are copied verbatim, doubled quotes included.
/// </para>
/// <para>
/// <see cref="DbDataReaderFetch"/> calls both methods to build the command every provider gets by
/// default. A second <see cref="IRemoteFetch"/> that builds its own <see cref="DbCommand"/> — a
/// vendor package, or a host's own — binds through the same two members, so a parameter means the
/// same thing whichever reader ran: the names <see cref="Rewrite"/> returns, in the order it
/// returns them, and the same <see cref="System.Data.DbType"/> per Chalk type. Nothing about the
/// scanner or the binder changed to make this public; only who may call it did.
/// </para>
/// </remarks>
public static class AdoParameters
{
    /// <summary>
    /// The query with its placeholders in <paramref name="style"/>, and the parameter names in
    /// placeholder order. A query with no placeholders comes back unchanged.
    /// </summary>
    /// <remarks>
    /// A positional style (<see cref="ParameterPlaceholder.Unspecified"/> or
    /// <see cref="ParameterPlaceholder.Question"/>) numbers placeholders from one rather than
    /// naming them — <c>1</c>, <c>2</c>, … — which is how SQLite looks up a bare <c>?</c>
    /// (<c>?1</c>, <c>?2</c>, …) and is exactly what a driver that binds purely by position reads
    /// correctly regardless of the name. Pair the returned names with the caller's values and types
    /// and hand all three, or the request they came from, to <c>Bind</c>.
    /// </remarks>
    public static (string Sql, IReadOnlyList<string> Names) Rewrite(string sql, ParameterPlaceholder style)
    {
        if (style is ParameterPlaceholder.Unspecified or ParameterPlaceholder.Question || !sql.Contains('?', StringComparison.Ordinal))
        {
            return (sql, PositionalNames(sql));
        }

        var text = new StringBuilder(sql.Length + 16);
        var names = new List<string>();
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i = CopyQuoted(sql, i, c, text);
                continue;
            }

            if (c == '?')
            {
                var name = NameOf(style, names.Count);
                names.Add(name);
                text.Append(Prefix(style)).Append(name);
                continue;
            }

            text.Append(c);
        }

        return (text.ToString(), names);
    }

    /// <summary>
    /// Binds <paramref name="values"/> onto <paramref name="command"/> under the names
    /// <see cref="Rewrite"/> produced, with the <see cref="System.Data.DbType"/> the column's Chalk
    /// type names — providers vary in what they infer from a bare CLR value, and saying so is what
    /// stops a decimal being bound as a double. A null value binds as <see cref="DBNull"/>.
    /// </summary>
    /// <remarks>
    /// This is the contract every <see cref="IRemoteFetch"/> implementation binds through: the same
    /// names, in the same order <see cref="Rewrite"/> handed back, and the same
    /// <see cref="System.Data.DbType"/> per Chalk type that <see cref="DbDataReaderFetch"/> binds —
    /// a second reader that bound differently would disagree with the first about what a parameter
    /// means, silently. <c>Bind(DbCommand, RemoteFetchRequest)</c> is the same binding read straight
    /// off a request, for a fetch that already has one.
    /// </remarks>
    public static void Bind(
        DbCommand command,
        IReadOnlyList<string> names,
        IReadOnlyList<object?> values,
        IReadOnlyList<ChalkType> types)
    {
        for (var i = 0; i < values.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = i < names.Count ? names[i] : $"p{i}";
            parameter.Value = values[i] ?? DBNull.Value;
            if (i < types.Count && values[i] is not null)
            {
                parameter.DbType = AdoTypeMapping.ToDbType(types[i]);
            }

            command.Parameters.Add(parameter);
        }
    }

    /// <summary>
    /// Binds <paramref name="request"/>'s <see cref="RemoteFetchRequest.ParameterNames"/>,
    /// <see cref="RemoteFetchRequest.Parameters"/> and <see cref="RemoteFetchRequest.ParameterTypes"/>
    /// onto <paramref name="command"/>, in that order — the same binding the four-argument overload
    /// does, read straight off the request so an <see cref="IRemoteFetch"/> implementation that
    /// already has one writes a single line and cannot mis-order the three lists.
    /// </summary>
    public static void Bind(DbCommand command, RemoteFetchRequest request) =>
        Bind(command, request.ParameterNames, request.Parameters, request.ParameterTypes);

    /// <summary>
    /// The names a positional query's parameters get. SQLite numbers bare <c>?</c> placeholders
    /// from one and looks them up as <c>?1</c>, <c>?2</c>, …; a driver that binds purely by position
    /// ignores the name, so this is the shape that satisfies both.
    /// </summary>
    private static IReadOnlyList<string> PositionalNames(string sql)
    {
        var names = new List<string>();
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, c);
                continue;
            }

            if (c == '?')
            {
                names.Add((names.Count + 1).ToString(CultureInfo.InvariantCulture));
            }
        }

        return names;
    }

    private static string Prefix(ParameterPlaceholder style) => style switch
    {
        ParameterPlaceholder.NamedAt => "@",
        ParameterPlaceholder.NamedDollar or ParameterPlaceholder.OrdinalDollar => "$",
        _ => string.Empty,
    };

    private static string NameOf(ParameterPlaceholder style, int ordinal) => style switch
    {
        ParameterPlaceholder.OrdinalDollar => (ordinal + 1).ToString(CultureInfo.InvariantCulture),
        _ => "p" + ordinal.ToString(CultureInfo.InvariantCulture),
    };

    private static int CopyQuoted(string sql, int start, char quote, StringBuilder text)
    {
        text.Append(quote);
        var i = start + 1;
        while (i < sql.Length)
        {
            var c = sql[i];
            text.Append(c);
            if (c == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    text.Append(quote);
                    i += 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return i - 1;
    }

    private static int SkipQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return i - 1;
    }
}
