namespace Chalk.Client;

/// <summary>
/// Which SQL dialect a statement is written in (D34). One value per
/// <c>org.apache.calcite.sql.validate.SqlConformanceEnum</c> constant, chosen per statement through
/// <see cref="PrepareOptions.Conformance"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Default"/> is standard SQL and is what a statement gets when nothing asks for
/// anything: <c>!=</c>, <c>%</c>, <c>GROUP BY</c> on a SELECT alias, <c>OFFSET start LIMIT count</c>
/// and <c>LIMIT start, count</c> are rejected under it and accepted under <see cref="Lenient"/> and
/// <see cref="Babel"/>.
/// </para>
/// <para>
/// Identifier quoting and case sensitivity are <em>not</em> part of this — they are pinned for every
/// dialect (D15): double quotes quote, unquoted identifiers keep their case, and matching is
/// case-insensitive. And <see cref="Babel"/> relaxes validation only; Babel-only <em>syntax</em>
/// needs Calcite's separate Babel parser, which Chalk does not ship.
/// </para>
/// </remarks>
public enum SqlConformance
{
    /// <summary>Standard SQL. The default.</summary>
    Default = 1,

    /// <summary>Accepts most of what the other dialects add, without being any one of them.</summary>
    Lenient = 2,

    /// <summary>As permissive as the core parser gets.</summary>
    Babel = 3,

    /// <summary>SQL:92.</summary>
    Strict92 = 4,

    /// <summary>SQL:99, strictly.</summary>
    Strict99 = 5,

    /// <summary>SQL:99, pragmatically.</summary>
    Pragmatic99 = 6,

    /// <summary>SQL:2003, strictly.</summary>
    Strict2003 = 7,

    /// <summary>SQL:2003, pragmatically.</summary>
    Pragmatic2003 = 8,

    /// <summary>MySQL 5.</summary>
    MySql5 = 9,

    /// <summary>Oracle 10.</summary>
    Oracle10 = 10,

    /// <summary>Oracle 12.</summary>
    Oracle12 = 11,

    /// <summary>SQL Server 2008.</summary>
    SqlServer2008 = 12,

    /// <summary>Presto.</summary>
    Presto = 13,

    /// <summary>BigQuery.</summary>
    BigQuery = 14,
}
