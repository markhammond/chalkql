using Chalk.Client;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The window corpus against the reference executor (D53, <c>13-window-functions.md</c> §6 and §7).
///
/// <para>
/// It is a class of its own for one reason: the reference executor materialises every frame, so a
/// running total over a 20 160-row partition costs two hundred million row visits. The queries
/// therefore run against <c>bars_small</c>, and this fixture keeps <c>lineitem</c> small too so that
/// the join expansion D54 may choose for the two-distinct query stays a comparison and not an
/// afternoon.
/// </para>
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class WindowDifferentialTests(SharedSidecar sidecar)
{
    /// <summary>The corpus family this class owns; <see cref="CorpusDifferentialTests"/> skips it.</summary>
    public const string Milestone = "m4-window";

    private static readonly CorpusFixture Fixture =
        CorpusFixture.Create(barMinutes: Fixtures.BarsSmallMinutes, lineItemRows: 4_000);

    /// <summary>One arena for the whole family, as the corpus suite does.</summary>
    private static readonly ExecutionArena HostArena =
        new(new ArenaOptions { RetainBytes = 8 << 20 });

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM4())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Vectorised_on_full_agrees_with_the_reference_executor_on_none(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await CompareAsync(Query(name), PushdownLevel.Full);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Both_engines_agree_on_the_same_reference_plan(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await CompareAsync(Query(name), PushdownLevel.None);
    }

    /// <summary>
    /// §7's arena claim: a window query leaves the arena empty. The operator rents the buffered
    /// input, the peer and frame arrays and one block per call, and every one of them comes back in
    /// the run's <c>finally</c> — including the run that ends in an exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task A_window_query_leaves_the_arena_empty(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = Query(name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        var (batches, stats) = await DifferentialRunner.RunAsync(engine, prepared, query, HostArena);
        try
        {
            Assert.Equal(stats.RowsProduced, batches.Sum(b => (long)b.Length));
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }

        Assert.Equal(0, HostArena.OutstandingBytes);
    }

    /// <summary>
    /// The same comparison at a batch size the window operator's emission loop cannot swallow: a
    /// partition then spans hundreds of batches, and a call that answered from the wrong row would
    /// show up as a shifted column.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Batch_boundaries_do_not_move_a_window_call(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await CompareAsync(Query(name), PushdownLevel.Full, batchSize: 7);
    }

    /// <summary>
    /// D258.4: the corpus down both window paths. A frame that ends at or before the current row takes
    /// the streaming operator; pinning the engine to <see cref="WindowExecution.Buffered"/> takes the
    /// one it replaced. The comparison is row by row and exact — the same plan on the same engine, so
    /// nothing about the order or the arithmetic is allowed to move.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Both_window_paths_produce_the_same_rows(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = Query(name);
        await using var streaming = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        await using var buffered = await CreateEngineAsync(
            ExecutionEngine.Vectorised, 4096, WindowExecution.Buffered);

        var streamingPrepared = await streaming.PrepareAsync(query.Sql, query.PrepareOptions());
        var bufferedPrepared = await buffered.PrepareAsync(query.Sql, query.PrepareOptions());

        var (actual, actualStats) = await DifferentialRunner.RunAsync(streaming, streamingPrepared, query);
        var (expected, expectedStats) =
            await DifferentialRunner.RunAsync(buffered, bufferedPrepared, query);

        try
        {
            // The same plan ran the same window nodes on both engines; only which operator each one
            // got is different.
            Assert.Equal(0, expectedStats.WindowsStreamed);
            Assert.Equal(
                expectedStats.WindowsBuffered,
                actualStats.WindowsStreamed + actualStats.WindowsBuffered);
            CorpusDifferentialTests.Compare(
                $"{query.Name}: streaming vs buffered "
                + $"({actualStats.WindowsStreamed} of {expectedStats.WindowsBuffered} streamed)",
                expected,
                actual,
                ResultComparisonOptions.Ordered);
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// The family's negative corpus is refused the same way on both paths: the window operator is
    /// chosen when a plan is compiled, and these never get that far.
    /// </summary>
    [Fact]
    public async Task The_negative_corpus_is_refused_on_both_window_paths()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var streaming = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        await using var buffered = await CreateEngineAsync(
            ExecutionEngine.Vectorised, 4096, WindowExecution.Buffered);

        var refused = 0;
        foreach (var query in CorpusQueries.LoadErrors()
            .Where(q => q.Milestone == Milestone + "-errors"))
        {
            var onStreaming = await Assert.ThrowsAsync<PlanningException>(
                () => streaming.PrepareAsync(query.Sql, query.PrepareOptions()).AsTask());
            var onBuffered = await Assert.ThrowsAsync<PlanningException>(
                () => buffered.PrepareAsync(query.Sql, query.PrepareOptions()).AsTask());

            Assert.Equal(onBuffered.Kind, onStreaming.Kind);
            refused++;
        }

        Assert.NotEqual(0, refused);
    }

    /// <summary>
    /// Which of the family's statements the streaming path of D258.4 actually takes, pinned to a
    /// number: a change that quietly stopped streaming a frame this operator covers would otherwise
    /// pass every other test in this class.
    /// </summary>
    [Fact]
    public async Task The_corpus_takes_the_streaming_path_where_the_frame_allows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        var streamed = new List<string>();
        var buffered = new List<string>();
        long windows = 0;

        foreach (var query in CorpusQueries.LoadM4())
        {
            var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());
            var (batches, stats) = await DifferentialRunner.RunAsync(engine, prepared, query);
            DifferentialRunner.Dispose(batches);

            windows += stats.WindowsStreamed + stats.WindowsBuffered;
            if (stats.WindowsStreamed > 0)
            {
                streamed.Add(query.Name);
            }

            if (stats.WindowsBuffered > 0)
            {
                buffered.Add(query.Name);
            }
        }

        var tally =
            $"{windows} window nodes; streaming: {string.Join(", ", streamed)}; "
            + $"buffered: {string.Join(", ", buffered)}";
        Assert.True(windows == 21, tally);
        Assert.True(streamed.Count == 12, tally);
        Assert.True(buffered.Count == 8, tally);
    }

    private static CorpusQuery Query(string name) =>
        CorpusQueries.LoadM4().Single(q => q.Name == name);

    private async Task CompareAsync(CorpusQuery query, PushdownLevel level, int batchSize = 4096)
    {
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised, batchSize);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference, batchSize);

        var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions(level));
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{query.Name}: vectorised@{level} vs reference@NONE at batch size {batchSize}",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(actualPlan, Fixture.Catalog));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    private async Task<ChalkEngine> CreateEngineAsync(
        ExecutionEngine engine,
        int batchSize,
        WindowExecution windows = WindowExecution.Automatic) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions
            {
                Engine = engine,
                BatchSize = batchSize,
                WindowExecution = windows,
            },
        });
}
