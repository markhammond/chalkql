using Chalk.Catalog;
using Chalk.Ir;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources;

/// <summary>
/// What the engine wants a source to run (D84, D85). The pushdown twin of <see cref="ScanRequest"/>:
/// everything below the <c>RemoteQuery</c> boundary in the plan, handed over as one query.
/// </summary>
/// <remarks>
/// Both flavours travel. A SQL source reads <see cref="QueryText"/> and binds
/// <see cref="Parameters"/>; a source whose query language is
/// <see cref="QueryLanguage.Ir"/> reads <see cref="PushedPlan"/> and interprets it itself. From M4 a
/// SQL source's request carries the pushed plan too, so nothing that generated the text is lost.
/// </remarks>
public sealed class RemoteQueryRequest
{
    /// <summary>The generated SQL, in the source's own dialect. Empty for an IR source.</summary>
    public required string QueryText { get; init; }

    /// <summary>
    /// The pushed subtree as IR — a <c>Read</c> with its filter and projection, and whatever
    /// <c>Filter</c>, <c>Project</c>, <c>Aggregate</c>, <c>Sort</c>, <c>Fetch</c> or <c>Join</c> the
    /// planner pushed above it. Never null from M4 on.
    /// </summary>
    public required Rel PushedPlan { get; init; }

    /// <summary>
    /// The bound parameter values, in the order the query's placeholders appear. Empty when the
    /// source does not take parameters, in which case the values are already inlined in
    /// <see cref="QueryText"/>.
    /// </summary>
    public required IReadOnlyList<object?> Parameters { get; init; }

    /// <summary>
    /// The logical type of each parameter, parallel to <see cref="Parameters"/>. Providers vary in
    /// what they infer from a bare CLR value — this is what stops a decimal being bound as a double
    /// — so the plan's own answer travels with the value.
    /// </summary>
    public required IReadOnlyList<ChalkType> ParameterTypes { get; init; }

    /// <summary>What every produced batch must match exactly — names, types, nullability, order.</summary>
    public required ArrowSchema OutputSchema { get; init; }

    /// <summary>Upper bound on rows per batch. A source may produce smaller batches.</summary>
    public required int BatchSize { get; init; }

    /// <summary>
    /// How long the source may take before the engine gives up on it. <c>null</c> means the source's
    /// own <see cref="SourceOptions.QueryTimeout"/> applies.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The dialect the query text is written in, as the schema declared it.</summary>
    public string Dialect { get; init; } = string.Empty;
}

/// <summary>
/// Per-source execution settings a host sets on the source builder (D86). Distinct from
/// <c>ExecutionOptions</c>, which is per query: these are properties of the connection to one
/// system, and one slow source should not need every query to be reconfigured.
/// </summary>
public sealed class SourceOptions
{
    /// <summary>The defaults: thirty seconds, no concurrency limit, no retries.</summary>
    public static SourceOptions Default { get; } = new();

    /// <summary>
    /// How long one query or scan may take. Exceeding it raises <see cref="SourceTimeoutException"/>
    /// naming the source. <see cref="Timeout.InfiniteTimeSpan"/> waits forever.
    /// </summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many queries the engine may have in flight against this source at once. Zero means no
    /// limit. Nothing reads it until M5's fan-out; it is here so an adapter can be written against
    /// the final shape.
    /// </summary>
    public int MaxConcurrentQueries { get; init; }

    /// <summary>
    /// Retries are the host's business, not Chalk's: a retry after a partial read would duplicate
    /// rows, and Chalk cannot know whether a failure was transient. Always <c>0</c> in v1.
    /// </summary>
    public int Retries => 0;
}
