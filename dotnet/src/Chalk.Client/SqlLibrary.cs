namespace Chalk.Client;

/// <summary>
/// One of Calcite's dialect function libraries (D60, <c>docs/design/14-windows-ii.md</c> §6). Naming
/// a library in <see cref="PrepareOptions.Libraries"/> adds that dialect's functions to the standard
/// operator table <em>for that statement</em>.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="SqlConformance"/>, which is about syntax and validation rather than about
/// which functions exist. A library function the IR has no mapping for still fails at planning as
/// <c>UNSUPPORTED</c> naming the function, so asking for a library never turns a missing feature into
/// a wrong answer.
/// </para>
/// <para>
/// <c>STRING_AGG</c> and <c>ARRAY_AGG</c> live in <see cref="Postgresql"/>, and
/// <c>ARRAY_TO_STRING</c> in <see cref="BigQuery"/>.
/// </para>
/// </remarks>
public enum SqlLibrary
{
    /// <summary>The standard operators, which are always available. Naming it changes nothing.</summary>
    Standard = 1,

    /// <summary>Google BigQuery.</summary>
    BigQuery = 2,

    /// <summary>Calcite's own extensions.</summary>
    Calcite = 3,

    /// <summary>ClickHouse.</summary>
    Clickhouse = 4,

    /// <summary>Apache Hive.</summary>
    Hive = 5,

    /// <summary>Microsoft SQL Server.</summary>
    Mssql = 6,

    /// <summary>MySQL.</summary>
    Mysql = 7,

    /// <summary>Oracle.</summary>
    Oracle = 8,

    /// <summary>PostgreSQL.</summary>
    Postgresql = 9,

    /// <summary>Amazon Redshift.</summary>
    Redshift = 10,

    /// <summary>Snowflake.</summary>
    Snowflake = 11,

    /// <summary>Apache Spark.</summary>
    Spark = 12,

    /// <summary>The spatial functions.</summary>
    Spatial = 13,

    /// <summary>Every library at once.</summary>
    All = 14,
}
