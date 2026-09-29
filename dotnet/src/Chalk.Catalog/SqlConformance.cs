namespace Chalk.Catalog;

/// <summary>
/// A SQL dialect level, by the name of Calcite's <c>SqlConformanceEnum</c> constant (D34, D318). A
/// request uses it to say which dialect its own SQL is written in
/// (<c>PrepareOptions.Conformance</c>); a <see cref="DialectProfileDescriptor"/> uses it to say
/// which SQL a source will accept.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Default"/> is standard SQL and is what a statement gets when nothing asks for
/// anything, and what <c>default(SqlConformance)</c> is: <c>!=</c>, <c>%</c>, <c>GROUP BY</c> on a
/// SELECT alias, <c>OFFSET start LIMIT count</c> and <c>LIMIT start, count</c> are rejected under it
/// and accepted under <see cref="Lenient"/> and <see cref="Babel"/>.
/// </para>
/// <para>
/// A name rather than an enum, so that a level a newer Calcite adds is usable through
/// <see cref="Named"/> without a new ChalkQL; the members here are the ones the sidecar's Calcite
/// has today. The sidecar lists what it accepts (<c>PlannerInfo.Conformances</c>) and refuses any
/// other name — at prepare for a request, at registration for a source.
/// </para>
/// <para>
/// Identifier quoting and case sensitivity are <em>not</em> part of this — they are pinned for every
/// dialect (D15): double quotes quote, unquoted identifiers keep their case, and matching is
/// case-insensitive. <see cref="Babel"/> alone is parsed by Calcite's Babel parser (D259).
/// </para>
/// </remarks>
public readonly struct SqlConformance : IEquatable<SqlConformance>
{
    private readonly string? _name;

    private SqlConformance(string name) => _name = name;

    /// <summary>Calcite's constant name: <c>DEFAULT</c>, <c>LENIENT</c>, <c>STRICT_2003</c>, ….</summary>
    public string Name => _name ?? "DEFAULT";

    /// <summary>Standard SQL. The default.</summary>
    public static SqlConformance Default { get; } = new("DEFAULT");

    /// <summary>Accepts most of what the other dialects add, without being any one of them.</summary>
    public static SqlConformance Lenient { get; } = new("LENIENT");

    /// <summary>As permissive as Calcite gets, and parsed by its Babel parser.</summary>
    public static SqlConformance Babel { get; } = new("BABEL");

    /// <summary>SQL:92.</summary>
    public static SqlConformance Strict92 { get; } = new("STRICT_92");

    /// <summary>SQL:99, strictly.</summary>
    public static SqlConformance Strict99 { get; } = new("STRICT_99");

    /// <summary>SQL:99, pragmatically.</summary>
    public static SqlConformance Pragmatic99 { get; } = new("PRAGMATIC_99");

    /// <summary>SQL:2003, strictly.</summary>
    public static SqlConformance Strict2003 { get; } = new("STRICT_2003");

    /// <summary>SQL:2003, pragmatically.</summary>
    public static SqlConformance Pragmatic2003 { get; } = new("PRAGMATIC_2003");

    /// <summary>MySQL 5.</summary>
    public static SqlConformance MySql5 { get; } = new("MYSQL_5");

    /// <summary>Oracle 10.</summary>
    public static SqlConformance Oracle10 { get; } = new("ORACLE_10");

    /// <summary>Oracle 12.</summary>
    public static SqlConformance Oracle12 { get; } = new("ORACLE_12");

    /// <summary>SQL Server 2008.</summary>
    public static SqlConformance SqlServer2008 { get; } = new("SQL_SERVER_2008");

    /// <summary>Presto.</summary>
    public static SqlConformance Presto { get; } = new("PRESTO");

    /// <summary>BigQuery.</summary>
    public static SqlConformance BigQuery { get; } = new("BIG_QUERY");

    /// <summary>
    /// The level Calcite names <paramref name="name"/>, spelled exactly as Calcite spells the
    /// constant. Only the spelling is checked here; the sidecar refuses a name it does not have.
    /// </summary>
    public static SqlConformance Named(string name) => new(CalciteNames.Check(name, "conformance level", "LENIENT or STRICT_2003", nameof(name)));

    public static bool operator ==(SqlConformance left, SqlConformance right) => left.Equals(right);

    public static bool operator !=(SqlConformance left, SqlConformance right) => !left.Equals(right);

    public bool Equals(SqlConformance other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SqlConformance other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>The name, as Calcite spells it.</summary>
    public override string ToString() => Name;
}
