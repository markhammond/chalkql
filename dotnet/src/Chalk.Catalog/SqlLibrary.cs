namespace Chalk.Catalog;

/// <summary>
/// One of Calcite's dialect function libraries, by the name of its <c>SqlLibrary</c> constant (D60,
/// D318). Naming a library in <c>PrepareOptions.Libraries</c> adds that dialect's functions to the
/// standard operator table <em>for that statement</em>; a <see cref="DialectProfileDescriptor"/>
/// names the ones its source implements natively.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="SqlConformance"/>, which is about syntax and validation rather than
/// about which functions exist. A library function the IR has no mapping for still fails at planning
/// as <c>UNSUPPORTED</c> naming the function, so asking for a library never turns a missing feature
/// into a wrong answer. <c>STRING_AGG</c> and <c>ARRAY_AGG</c> live in <see cref="Postgresql"/>, and
/// <c>ARRAY_TO_STRING</c> in <see cref="BigQuery"/>.
/// </para>
/// <para>
/// A name rather than an enum, for the reason <see cref="SqlConformance"/> is one: a library a newer
/// Calcite adds is usable through <see cref="Named"/>, and the sidecar refuses a name it does not
/// have (<c>PlannerInfo.Libraries</c> lists the ones it does). <c>default(SqlLibrary)</c> is
/// <see cref="Standard"/>.
/// </para>
/// </remarks>
public readonly struct SqlLibrary : IEquatable<SqlLibrary>
{
    private readonly string? _name;

    private SqlLibrary(string name) => _name = name;

    /// <summary>Calcite's constant name: <c>STANDARD</c>, <c>POSTGRESQL</c>, <c>BIG_QUERY</c>, ….</summary>
    public string Name => _name ?? "STANDARD";

    /// <summary>The standard operators, which are always available. Naming it changes nothing.</summary>
    public static SqlLibrary Standard { get; } = new("STANDARD");

    /// <summary>Google BigQuery.</summary>
    public static SqlLibrary BigQuery { get; } = new("BIG_QUERY");

    /// <summary>Calcite's own extensions.</summary>
    public static SqlLibrary Calcite { get; } = new("CALCITE");

    /// <summary>ClickHouse.</summary>
    public static SqlLibrary Clickhouse { get; } = new("CLICKHOUSE");

    /// <summary>Apache Hive.</summary>
    public static SqlLibrary Hive { get; } = new("HIVE");

    /// <summary>Microsoft SQL Server.</summary>
    public static SqlLibrary Mssql { get; } = new("MSSQL");

    /// <summary>MySQL.</summary>
    public static SqlLibrary Mysql { get; } = new("MYSQL");

    /// <summary>Oracle.</summary>
    public static SqlLibrary Oracle { get; } = new("ORACLE");

    /// <summary>PostgreSQL.</summary>
    public static SqlLibrary Postgresql { get; } = new("POSTGRESQL");

    /// <summary>Amazon Redshift.</summary>
    public static SqlLibrary Redshift { get; } = new("REDSHIFT");

    /// <summary>Snowflake.</summary>
    public static SqlLibrary Snowflake { get; } = new("SNOWFLAKE");

    /// <summary>Apache Spark.</summary>
    public static SqlLibrary Spark { get; } = new("SPARK");

    /// <summary>The spatial functions.</summary>
    public static SqlLibrary Spatial { get; } = new("SPATIAL");

    /// <summary>Every library at once.</summary>
    public static SqlLibrary All { get; } = new("ALL");

    /// <summary>
    /// The library Calcite names <paramref name="name"/>, spelled exactly as Calcite spells the
    /// constant. Only the spelling is checked here; the sidecar refuses a name it does not have.
    /// </summary>
    public static SqlLibrary Named(string name) => new(CalciteNames.Check(name, "library", "POSTGRESQL or BIG_QUERY", nameof(name)));

    public static bool operator ==(SqlLibrary left, SqlLibrary right) => left.Equals(right);

    public static bool operator !=(SqlLibrary left, SqlLibrary right) => !left.Equals(right);

    public bool Equals(SqlLibrary other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SqlLibrary other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>The name, as Calcite spells it.</summary>
    public override string ToString() => Name;
}
