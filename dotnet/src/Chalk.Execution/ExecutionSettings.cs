namespace Chalk.Execution;

/// <summary>
/// What the client tells the engine about one execution. The mirror of the public
/// <c>ExecutionOptions</c> (<c>docs/design/04-client.md</c> §2), reduced to what the engine acts on.
/// </summary>
internal sealed class ExecutionSettings
{
    /// <summary>Upper bound on rows per batch, end to end: sources, operators and output.</summary>
    public int BatchSize { get; init; } = 4096;

    /// <summary>Run the row-at-a-time reference interpreter instead (D13, invariant I4).</summary>
    public bool UseReferenceEngine { get; init; }

    /// <summary>
    /// Compile every window onto the buffered operator, whatever its frame (D258.4). The escape hatch
    /// behind the public <c>WindowExecution.Buffered</c>, and what runs the corpus down both paths.
    /// </summary>
    public bool ForceBufferedWindows { get; init; }

    /// <summary>
    /// Back output batches with pooled memory the host must dispose. The default is managed arrays,
    /// so a host may keep a batch forever (§6.1).
    /// </summary>
    public bool PooledOutput { get; init; }

    /// <summary>
    /// The Arrow layouts the host accepts for a STRING output column (D244). A single layout is
    /// declared for every STRING column; <see cref="Chalk.Sources.StringLayouts.Any"/> lets the
    /// compiler choose per column, at prepare time, from the plan and the sources' own layouts.
    /// </summary>
    public Chalk.Sources.StringLayouts OutputStrings { get; init; } =
        Chalk.Sources.StringLayouts.Utf8View;

    /// <summary>The clock <c>CURRENT_TIMESTAMP</c> would read. Injectable so tests are deterministic.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Stamp every column view with its producing batch's generation and check it on access (D61).
    /// On by default in Debug builds; a host turns it on in Release to hunt a lifetime bug.
    /// </summary>
    public bool ValidateBatchLifetimes { get; init; }
#if DEBUG
        = true;
#endif

    /// <summary>
    /// The fraction of rows a filter must keep to forward a selection vector rather than compacting
    /// (D62). Zero always forwards, one always compacts; 0.5 is the default of §2.
    /// </summary>
    public double SelectionCompactionThreshold { get; init; } = 0.5;

    /// <summary>
    /// Per-source settings, by source id (D86). A source the host said nothing about is left to what
    /// its own builder declared, or <see cref="Chalk.Sources.SourceOptions.Default"/> where that said
    /// nothing either.
    /// </summary>
    public IReadOnlyDictionary<string, Chalk.Sources.SourceOptions> SourceOptions { get; init; } =
        new Dictionary<string, Chalk.Sources.SourceOptions>(StringComparer.Ordinal);

    /// <summary>
    /// The client-bodied function implementations this engine was given (D79). Empty unless the host
    /// registered some; a catalog that declares one without an implementation never gets this far,
    /// because <c>ChalkEngine.CreateAsync</c> refuses it.
    /// </summary>
    public Chalk.Sources.HostFunctionSet Functions { get; init; } = Chalk.Sources.HostFunctionSet.Empty;

    /// <summary>Arrow batches a remote stream may run ahead of the pipeline (D107). Zero is off.</summary>
    public int RemotePrefetchDepth { get; init; } = 2;

    /// <summary>Remote queries this execution may have in flight at once, over every source. Zero is unlimited.</summary>
    public int MaxRemoteConcurrency { get; init; } = Environment.ProcessorCount;

    /// <summary>How long an execution waits for its in-flight fetches to observe cancellation (D108).</summary>
    public TimeSpan CancellationGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <paramref name="sourceId"/> may take over one query, when the host said so here;
    /// <see langword="null"/> when it did not. A request carrying no timeout leaves the source to the
    /// <c>QueryTimeout</c> its own builder declared (D86) — the host's entry overrides the adapter's,
    /// and its absence is not the thirty-second default stamped over whatever the adapter chose.
    /// </summary>
    public TimeSpan? SourceTimeout(string sourceId) =>
        SourceOptions.TryGetValue(sourceId, out var options)
            ? options.QueryTimeout
            : null;

    /// <summary>
    /// Remote queries this execution may have in flight against <paramref name="sourceId"/>. Zero is
    /// unlimited, which is what a source whose adapter said nothing gets.
    /// </summary>
    public int MaxConcurrentQueries(string sourceId) =>
        SourceOptions.TryGetValue(sourceId, out var options)
            ? options.MaxConcurrentQueries
            : 0;
}
