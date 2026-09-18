using System.Text;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Ado;

/// <summary>One discovered or declared table, and the scan SQL it produces.</summary>
internal sealed class AdoTableDefinition
{
    private readonly ChalkType[] _types;

    public AdoTableDefinition(TableDescriptor descriptor, string remoteName)
    {
        Descriptor = descriptor;
        RemoteName = remoteName;
        _types = [.. descriptor.Columns.Select(c => c.Type)];
    }

    /// <summary>The name Chalk addresses this table by.</summary>
    public string Name => Descriptor.Name;

    /// <summary>The name the source knows it by, which may be schema-qualified.</summary>
    public string RemoteName { get; }

    public TableDescriptor Descriptor { get; }

    /// <summary>The logical types of the projected columns, in output order.</summary>
    public IReadOnlyList<ChalkType> ColumnTypes(IReadOnlyList<int> projection)
    {
        var types = new ChalkType[projection.Count];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = _types[projection[i]];
        }

        return types;
    }

    /// <summary>
    /// The scan: <c>SELECT columns FROM table</c> in the source's dialect. No <c>ORDER BY</c> — a
    /// SQL source's scan order is not a promise anyone may rely on, which is why
    /// <see cref="AdoSourceBuilder"/> declares no collations either.
    /// </summary>
    public string SelectSql(IReadOnlyList<int> projection, DialectProfileDescriptor profile)
    {
        var sql = new StringBuilder("SELECT ");
        for (var i = 0; i < projection.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            AdoQuoting.AppendIdentifier(sql, Descriptor.Columns[projection[i]].Name, profile);
        }

        sql.Append(" FROM ");
        AdoQuoting.AppendQualifiedName(sql, RemoteName, profile);
        return sql.ToString();
    }
}

/// <summary>
/// Quoting identifiers the way a source's dialect profile says (§1). The planner does the same job
/// for a pushed query through Calcite's <c>SqlDialect</c>; this is the scan path's copy, and it is
/// deliberately small — the only identifiers it ever writes are ones the catalog already validated.
/// </summary>
internal static class AdoQuoting
{
    /// <summary>Appends one identifier, quoted and escaped for this profile.</summary>
    public static void AppendIdentifier(StringBuilder sql, string name, DialectProfileDescriptor profile)
    {
        var (open, close) = Quotes(profile.Quoting);
        if (open == '\0')
        {
            sql.Append(name);
            return;
        }

        sql.Append(open);
        foreach (var c in name)
        {
            if (c == close)
            {
                sql.Append(close);
            }

            sql.Append(c);
        }

        sql.Append(close);
    }

    /// <summary>Appends a possibly schema-qualified name, quoting each part separately.</summary>
    public static void AppendQualifiedName(
        StringBuilder sql, string name, DialectProfileDescriptor profile)
    {
        var parts = name.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                sql.Append('.');
            }

            AppendIdentifier(sql, parts[i], profile);
        }
    }

    private static (char Open, char Close) Quotes(IdentifierQuoting quoting) => quoting switch
    {
        IdentifierQuoting.None => ('\0', '\0'),
        IdentifierQuoting.BackTick => ('`', '`'),
        IdentifierQuoting.Bracket => ('[', ']'),
        _ => ('"', '"'),
    };
}
