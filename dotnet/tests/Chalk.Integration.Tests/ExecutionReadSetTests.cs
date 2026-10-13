using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An execution reads every table at one epoch (F188): it pins what each table serves when it begins,
/// so a refresh transaction that commits while it runs is seen by none of its scans, however late a
/// scan starts — the probe side of a join, which starts only once the build side is read. Before, a
/// scan took its table's snapshot when it started, and a join could read one table before a refresh
/// and the other after it.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ExecutionReadSetTests(SharedSidecar sidecar)
{
    private sealed record Left(int Id, int V);

    private sealed record Right(int Id, int W);

    private const string Joined =
        "SELECT l.id, l.v, r.w FROM lefts l JOIN rights r ON l.id = r.id WHERE trip(l.id) AND trip(r.id)";

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_refresh_that_commits_while_a_join_reads_is_seen_by_neither_side(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tables = Pair(1);
        ChalkEngine? engine = null;
        var tripped = 0;
        engine = await ChalkEngine.CreateAsync(
            Options(tables, executor, () => Trip(ref tripped, engine!, tables)), TestContext.Current.CancellationToken);
        await using var _ = engine;

        var during = await RowsAsync(engine);
        Assert.Equal(1, tripped);
        Assert.Equal(50, during.Count);
        Assert.All(during, row => Assert.Equal((1, 1), row));

        var after = await RowsAsync(engine);
        Assert.All(after, row => Assert.Equal((2, 2), row));
    }

    /// <summary>
    /// A source is shared between engines by default, so the gate is the process's, not an engine's:
    /// a transaction another engine commits while this one's join reads is seen by neither side.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_refresh_another_engine_commits_while_a_join_reads_is_seen_by_neither_side(
        ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tables = Pair(1);
        await using var other = await ChalkEngine.CreateAsync(
            Options(tables, executor, () => { }), TestContext.Current.CancellationToken);
        var tripped = 0;
        await using var engine = await ChalkEngine.CreateAsync(
            Options(tables, executor, () => Trip(ref tripped, other, tables)), TestContext.Current.CancellationToken);

        var during = await RowsAsync(engine);
        Assert.Equal(1, tripped);
        Assert.Equal(50, during.Count);
        Assert.All(during, row => Assert.Equal((1, 1), row));

        var after = await RowsAsync(engine);
        Assert.All(after, row => Assert.Equal((2, 2), row));
    }

    /// <summary>
    /// Executions racing a stream of transactions each read one: every transaction sets both tables
    /// to its own number, and every row any execution returns has the two numbers equal.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Executions_racing_refreshes_each_read_one_transaction(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var tables = Pair(0);
        await using var engine = await ChalkEngine.CreateAsync(
            Options(tables, executor, () => { }), TestContext.Current.CancellationToken);
        var prepared = await engine.PrepareAsync(
            "SELECT l.v, r.w FROM lefts l JOIN rights r ON l.id = r.id", ct: TestContext.Current.CancellationToken);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refreshes = Task.Run(async () =>
        {
            for (var k = 1; !stop.IsCancellationRequested; k++)
            {
                await Replace(engine, tables, k);
            }
        });

        var torn = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                foreach (var (v, w) in await RowsAsync(engine, prepared))
                {
                    if (v != w)
                    {
                        Interlocked.Increment(ref torn);
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(readers);
        await stop.CancelAsync();
        await refreshes;
        Assert.Equal(0, torn);
    }

    /// <summary>
    /// What an execution pinned it lets go of when it ends: the snapshot it held is reported released
    /// once the refresh has replaced it and nothing reads it any more.
    /// </summary>
    [Fact]
    public async Task An_execution_lets_its_pinned_tables_go_when_it_ends()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        IReadOnlyList<Left> first = [new(1, 1), new(2, 1)];
        var tables = new PocoSourceBuilder("pair")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("lefts", first)
            .Build();
        var released = new List<object>();
        tables.SnapshotReleased += release => released.Add(release.Collection);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [tables],
            Planner = sidecar.CreatePlanner(),
        }, TestContext.Current.CancellationToken);

        var prepared = await engine.PrepareAsync("SELECT id FROM lefts", ct: TestContext.Current.CancellationToken);
        await using (var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[], ct: TestContext.Current.CancellationToken))
        {
            await using var batches = execution.Batches.GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await batches.MoveNextAsync());
            batches.Current.Dispose();

            await engine.RefreshAsync(r => r.Replace(tables, "lefts", (IReadOnlyList<Left>)[new(3, 2)]), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(first, released);

            while (await batches.MoveNextAsync())
            {
                batches.Current.Dispose();
            }
        }

        Assert.Contains(first, released);
    }

    /// <summary>Two tables of fifty rows each, every <c>v</c> and every <c>w</c> set to <paramref name="value"/>.</summary>
    private static PocoSource Pair(int value) => new PocoSourceBuilder("pair")
        .NamingPolicy(PocoNamingPolicy.SnakeCase)
        .AddTable("lefts", Lefts(value))
        .AddTable("rights", Rights(value))
        .AddFunction("trip", f => f.Scalar<int, bool>("id").Strict().Client())
        .Build();

    private static IReadOnlyList<Left> Lefts(int value) => [.. Enumerable.Range(1, 50).Select(i => new Left(i, value))];

    private static IReadOnlyList<Right> Rights(int value) => [.. Enumerable.Range(1, 50).Select(i => new Right(i, value))];

    /// <summary>An engine over <paramref name="tables"/> whose <c>trip</c> calls <paramref name="onCall"/> and passes every row.</summary>
    private ChalkEngineOptions Options(PocoSource tables, ExecutionEngine executor, Action onCall) => new()
    {
        Sources = [tables],
        Planner = sidecar.CreatePlanner(),
        Execution = new ExecutionOptions { Engine = executor, BatchSize = 8 },
        Functions = registry => registry.AddScalar<int, bool>("trip", _ =>
        {
            onCall();
            return true;
        }),
    };

    /// <summary>
    /// The first call, from whichever side of the join reads first, commits a transaction through
    /// <paramref name="refresher"/> setting both tables to 2, and waits for it.
    /// </summary>
    private static void Trip(ref int tripped, ChalkEngine refresher, PocoSource tables)
    {
        if (Interlocked.Exchange(ref tripped, 1) == 0)
        {
            Replace(refresher, tables, 2).GetAwaiter().GetResult();
        }
    }

    private static Task Replace(ChalkEngine engine, PocoSource tables, int value) => engine
        .RefreshAsync(r => r.Replace(tables, "lefts", Lefts(value)).Replace(tables, "rights", Rights(value)))
        .AsTask();

    private static async Task<List<(int V, int W)>> RowsAsync(ChalkEngine engine)
    {
        var rows = new List<(int, int)>();
        await foreach (var batch in engine.QueryAsync(Joined, ct: TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                Collect(batch, 1, rows);
            }
        }

        return rows;
    }

    private static async Task<List<(int V, int W)>> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<(int, int)>();
        await using var execution = await engine.ExecuteAsync(
            prepared, (IReadOnlyList<object?>)[], ct: TestContext.Current.CancellationToken);
        await foreach (var batch in execution.Batches.WithCancellation(TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                Collect(batch, 0, rows);
            }
        }

        return rows;
    }

    private static void Collect(RecordBatch batch, int first, List<(int, int)> rows)
    {
        var v = (Int32Array)batch.Column(first);
        var w = (Int32Array)batch.Column(first + 1);
        for (var i = 0; i < batch.Length; i++)
        {
            rows.Add((v.GetValue(i)!.Value, w.GetValue(i)!.Value));
        }
    }
}
