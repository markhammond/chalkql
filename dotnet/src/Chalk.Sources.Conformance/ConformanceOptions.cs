using Apache.Arrow;

namespace Chalk.Sources.Conformance;

/// <summary>How one conformance run is set up (D88).</summary>
public sealed class ConformanceOptions
{
    /// <summary>The defaults: the kit's own table name, no seeding, every check.</summary>
    public static ConformanceOptions Default { get; } = new();

    /// <summary>
    /// The table the kit reads. Defaults to <see cref="ConformanceDataset.DefaultTable"/>; point it
    /// at an existing table when that table already has the kit's schema and rows.
    /// </summary>
    public string Table { get; init; } = ConformanceDataset.DefaultTable;

    /// <summary>
    /// Loads <see cref="ConformanceDataset"/>'s rows into the source. Called once, before anything
    /// else, and given the rows in both shapes: the Arrow batch and the SQL text. An adapter that
    /// is already pointed at a table holding them leaves this null.
    /// </summary>
    public Func<ConformanceSeed, CancellationToken, Task>? Seed { get; init; }

    /// <summary>
    /// Which checks to run. Every one by default; narrowing is for a source that cannot be seeded,
    /// or a run that is chasing one finding.
    /// </summary>
    public ConformanceChecks Checks { get; init; } = ConformanceChecks.All;

    /// <summary>
    /// Rows per batch when the kit reads the source. Small on purpose: the dataset is sixteen rows
    /// and a source that ignores <c>BatchSize</c> is worth catching here too.
    /// </summary>
    public int BatchSize { get; init; } = 8;

    /// <summary>
    /// What the run was pointed at, recorded verbatim in the report's header — a server version, a
    /// build, a file name. A dialect difference is a property of a <em>version</em>, so a checked-in
    /// report that does not say which one it observed is a diff nobody can read.
    /// </summary>
    public string Server { get; init; } = string.Empty;
}

/// <summary>What the kit hands a seeding adapter: the rows, in both shapes it might want them.</summary>
public sealed class ConformanceSeed
{
    /// <summary>The source being seeded.</summary>
    public required ISourceRuntime Source { get; init; }

    /// <summary>The table to create and fill.</summary>
    public required string Table { get; init; }

    /// <summary>The rows as one Arrow batch. The caller disposes it.</summary>
    public required RecordBatch Batch { get; init; }

    /// <summary>The <c>CREATE TABLE</c> in this source's dialect.</summary>
    public required string CreateTable { get; init; }

    /// <summary>The <c>INSERT</c>s in this source's dialect, in row order.</summary>
    public required IReadOnlyList<string> Inserts { get; init; }
}

/// <summary>Which families of check a run performs.</summary>
[Flags]
public enum ConformanceChecks
{
    None = 0,

    /// <summary>
    /// Every declared capability run on and off, compared against the source's own scan (D88 (a)).
    /// A false positive or a false negative on any pushed shape fails the run naming the shape.
    /// </summary>
    Capabilities = 1,

    /// <summary>
    /// The dialect probes (§5 (b)): string collation, NULL placement, integer division and modulus
    /// of negatives, decimal precision, timestamp precision, <c>LIKE</c> escaping, <c>BETWEEN</c>
    /// inclusivity, empty string versus NULL.
    /// </summary>
    Dialect = 2,

    /// <summary>
    /// Every declared unique key and unique index checked against the data (F16(e)). The rules that
    /// delete a join read column uniqueness, so a unique key that is not unique is a wrong answer.
    /// </summary>
    Uniqueness = 4,

    All = Capabilities | Dialect | Uniqueness,
}
