using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Tests;

/// <summary>
/// The federation operators against a fake remote source (§7): what the lookup join sends, what the
/// adaptive join decides, what the fan-out overlaps, and what happens when a source is slow, is
/// broken, or is cancelled.
/// </summary>
/// <remarks>
/// Every plan here is hand-built. That is deliberate: the corpus already asserts that the planner
/// produces these shapes and that they answer correctly, and what is left to pin down is the
/// operator's own behaviour at the boundaries — the key-batch edge, a NULL key, an outer join's
/// unmatched pass, a cancelled fetch. A hand-built plan is the only way to sit exactly on one.
/// </remarks>
public sealed class FederationOperatorTests
{
    private static readonly TestTable Drivers = new()
    {
        Name = "drivers",
        Columns = [("k", ChalkType.String()), ("label", ChalkType.String())],
        Rows =
        [
            ["a", "A"],
            ["b", "B"],
            ["c", "C"],
            [null, "none"],
            ["a", "A again"],
            ["z", "missing"],
        ],
    };

    private static readonly TestTable Facts = new()
    {
        Name = "facts",
        Columns = [("k", ChalkType.String()), ("v", ChalkType.Int64())],
        Rows =
        [
            ["a", 1L],
            ["a", 2L],
            ["b", 3L],
            ["c", 4L],
            ["d", 5L],
        ],
    };

    private static (TestSource Local, FakeRemoteSource Remote, CatalogContext Catalog) Fixture()
    {
        var local = new TestSource("mem", "main", Drivers);
        var remote = new FakeRemoteSource("far", Facts);
        return (local, remote, FakeRemoteSource.Catalog(local, remote));
    }

    private static Plan LookupPlan(
        JoinType type = JoinType.Inner, int maxKeysPerCall = 2, bool keySetRows = false)
    {
        var driving = IrBuilder.Read(Drivers.Name, Drivers.RowType());
        var lookup = IrBuilder.RemoteQuery(
            "far",
            "SELECT k, v FROM facts WHERE k IN (?)",
            Facts.RowType(),
            parameters: [IrBuilder.KeySet(IrBuilder.Str())]);
        return IrBuilder.Plan(
            IrBuilder.LookupJoin(driving, lookup, 0, 0, type, maxKeysPerCall, keySetRows));
    }

    private static async Task<(List<object?[]> Rows, ExecutionStats Stats)> RunAsync(
        Plan plan,
        TestSource local,
        FakeRemoteSource remote,
        CatalogContext catalog,
        ExecutionSettings? settings = null,
        IReadOnlyDictionary<string, string>? redactedQueryText = null)
    {
        var compiled = PlanCompiler.Compile(
            plan,
            catalog,
            new Dictionary<string, ISourceRuntime>(StringComparer.Ordinal)
            {
                [local.SourceId] = local,
                [remote.SourceId] = remote,
            },
            settings ?? new ExecutionSettings { BatchSize = 4096, ValidateBatchLifetimes = true },
            redactedQueryText);

        var stats = new ExecutionStats();
        var rows = new List<object?[]>();
        await foreach (var batch in compiled.ExecuteAsync(
            [], stats, arena: null, TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch));
            }
        }

        return (rows, stats);
    }

    // ---------------------------------------------------------------- lookup batching

    /// <summary>
    /// Keys are batched at <c>max_keys_per_call</c> <em>distinct non-NULL</em> keys. Four distinct
    /// keys at two per call is two calls, whatever the row count — a hundred rows over five symbols
    /// is one call, not a hundred.
    /// </summary>
    [Fact]
    public async Task A_lookup_sends_distinct_non_null_keys_in_batches_of_max_keys()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, stats) = await RunAsync(LookupPlan(maxKeysPerCall: 2), local, remote, catalog);

        // Six driving rows over four distinct keys, two keys per call: {a, b}, then {c, a}, then
        // {z}. The buffer is cleared with each call, so a key that appears on both sides of a
        // boundary is asked for twice — which is the cost of never holding more than one call's
        // worth of driving rows, and is why the batching is on *distinct keys within a call*.
        Assert.Equal(3, remote.Calls.Count);
        Assert.Equal(3, stats.RemoteCalls);
        Assert.All(remote.Calls, keys => Assert.True(keys.Length <= 2));
        Assert.DoesNotContain(remote.Calls.SelectMany(k => k), key => key is null);
        Assert.Equal(
            ["a", "b", "c", "z"],
            remote.Calls.SelectMany(k => k).Select(k => (string?)k).Distinct().Order(StringComparer.Ordinal));

        // a→1,2 twice (two driving rows), b→3, c→4. z and NULL match nothing.
        Assert.Equal(6, rows.Count);
    }

    /// <summary>One call when every key fits, and the whole driving side buffered once.</summary>
    [Fact]
    public async Task A_lookup_that_fits_makes_one_call()
    {
        var (local, remote, catalog) = Fixture();

        var (_, stats) = await RunAsync(LookupPlan(maxKeysPerCall: 100), local, remote, catalog);

        Assert.Single(remote.Calls);
        Assert.Equal(1, stats.RemoteCalls);
    }

    /// <summary>
    /// <c>RowsFetched</c> is the matching rows and not the table: the number a host reads to see
    /// whether the strategy is doing anything (§6 corpus 01).
    /// </summary>
    [Fact]
    public async Task A_lookup_fetches_only_the_matching_rows()
    {
        var (local, remote, catalog) = Fixture();

        var (_, stats) = await RunAsync(LookupPlan(maxKeysPerCall: 100), local, remote, catalog);

        // a, a, b, c — never `d`, which no driving row asks for.
        Assert.Equal(4, stats.RowsFetched);
    }

    /// <summary>A NULL key matches nothing, so an INNER join drops the row that has one.</summary>
    [Fact]
    public async Task A_null_key_never_matches()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, _) = await RunAsync(LookupPlan(), local, remote, catalog);

        Assert.DoesNotContain(rows, row => row[1] as string == "none");
    }

    /// <summary>LEFT keeps the unmatched driving rows, NULL-padded — the NULL key included.</summary>
    [Fact]
    public async Task A_left_lookup_pads_the_rows_that_matched_nothing()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, _) = await RunAsync(LookupPlan(JoinType.Left), local, remote, catalog);

        Assert.Equal(8, rows.Count);
        var padded = rows.Where(r => r[2] is null).Select(r => (string?)r[1]).Order(StringComparer.Ordinal);
        Assert.Equal(["missing", "none"], padded);
    }

    /// <summary>SEMI emits each matched driving row once, whatever it matched.</summary>
    [Fact]
    public async Task A_semi_lookup_emits_each_matched_driving_row_once()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, _) = await RunAsync(LookupPlan(JoinType.Semi), local, remote, catalog);

        Assert.Equal(2, rows[0].Length);
        Assert.Equal(
            ["A", "A again", "B", "C"],
            rows.Select(r => (string?)r[1]).Order(StringComparer.Ordinal));
    }

    /// <summary>ANTI emits the driving rows that matched nothing — the NULL key among them.</summary>
    [Fact]
    public async Task An_anti_lookup_emits_the_rows_that_matched_nothing()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, _) = await RunAsync(LookupPlan(JoinType.Anti), local, remote, catalog);

        Assert.Equal(
            ["missing", "none"],
            rows.Select(r => (string?)r[1]).Order(StringComparer.Ordinal));
    }

    /// <summary>The broadcast spelling ships the same keys and gives the same answer (§1).</summary>
    [Fact]
    public async Task The_rows_spelling_of_a_key_set_answers_the_same()
    {
        var (local, remote, catalog) = Fixture();
        var (asList, _) = await RunAsync(LookupPlan(maxKeysPerCall: 100), local, remote, catalog);

        var (local2, remote2, catalog2) = Fixture();
        var (asRows, _) = await RunAsync(
            LookupPlan(maxKeysPerCall: 100, keySetRows: true), local2, remote2, catalog2);

        Assert.Equal(asList.Count, asRows.Count);
        Assert.Single(remote2.Calls);
    }

    /// <summary>The two spellings differ in the SQL, which is the only thing that differs.</summary>
    [Fact]
    public void A_key_set_expands_as_a_list_or_as_rows()
    {
        const string Sql = "SELECT k FROM facts WHERE k IN (?)";

        Assert.Equal(
            "SELECT k FROM facts WHERE k IN (?, ?, ?)",
            Operators.KeySetQuery.Expand(Sql, 0, 3, asRows: false));
        Assert.Equal(
            "SELECT k FROM facts WHERE k IN (?), (?), (?)",
            Operators.KeySetQuery.Expand(Sql, 0, 3, asRows: true));
    }

    /// <summary>
    /// A key set over several columns expands a <em>row</em> at a time (F50): the one placeholder
    /// the planner wrote becomes a row-constructor list, which is what
    /// <c>supports_row_value_in_list</c> says the source accepts.
    /// </summary>
    [Fact]
    public void A_composite_key_set_expands_as_a_list_of_row_constructors()
    {
        const string Sql = "SELECT k FROM facts WHERE (a, b) IN (?)";

        Assert.Equal(
            "SELECT k FROM facts WHERE (a, b) IN ((?, ?), (?, ?), (?, ?))",
            Operators.KeySetQuery.Expand(Sql, 0, 3, asRows: false, columns: 2));

        // One column is the spelling it always was, whatever the parameter says.
        Assert.Equal(
            "SELECT k FROM facts WHERE k IN (?, ?)",
            Operators.KeySetQuery.Expand(
                "SELECT k FROM facts WHERE k IN (?)", 0, 2, asRows: false, columns: 1));
    }

    /// <summary>A <c>?</c> inside a literal is data, not a placeholder.</summary>
    [Fact]
    public void A_question_mark_inside_a_literal_is_not_a_placeholder()
    {
        Assert.Equal(
            "SELECT '?' AS q FROM t WHERE k IN (?, ?)",
            Operators.KeySetQuery.Expand("SELECT '?' AS q FROM t WHERE k IN (?)", 0, 2, asRows: false));
        Assert.Equal(
            "SELECT \"a?b\" FROM t WHERE k IN (?, ?)",
            Operators.KeySetQuery.Expand("SELECT \"a?b\" FROM t WHERE k IN (?)", 0, 2, asRows: false));
    }

    // ---------------------------------------------------------------- adaptive

    private static Plan AdaptivePlan(int maxKeys)
    {
        var small = IrBuilder.Read(Drivers.Name, Drivers.RowType());
        var replay = IrBuilder.MaterialisedInput(small);
        var lookupSide = IrBuilder.RemoteQuery(
            "far",
            "SELECT k, v FROM facts WHERE k IN (?)",
            Facts.RowType(),
            parameters: [IrBuilder.KeySet(IrBuilder.Str())]);
        var lookup = IrBuilder.LookupJoin(replay, lookupSide, 0, 0, maxKeysPerCall: 100);

        var whole = IrBuilder.RemoteQuery("far", "SELECT k, v FROM facts", Facts.RowType());
        var local = IrBuilder.HashJoin(replay, whole, [0], [0]);

        return IrBuilder.Plan(IrBuilder.AdaptiveJoin(small, lookup, local, key: 0, maxKeys: maxKeys));
    }

    /// <summary>
    /// Four distinct keys under a threshold of ten: the lookup branch runs and the decision says so.
    /// </summary>
    [Fact]
    public async Task An_adaptive_join_takes_the_lookup_branch_when_the_keys_fit()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, stats) = await RunAsync(AdaptivePlan(maxKeys: 10), local, remote, catalog);

        var decision = Assert.Single(stats.AdaptiveDecisions);
        Assert.Equal(AdaptiveBranch.Lookup, decision.Branch);
        Assert.Equal(4, decision.DistinctKeys);
        Assert.Equal(6, decision.SmallRows);
        Assert.Equal(10, decision.MaxKeys);
        Assert.Equal(6, rows.Count);
        Assert.Equal(4, stats.RowsFetched);
    }

    /// <summary>The same plan and the same rows, over a threshold the key count exceeds.</summary>
    [Fact]
    public async Task An_adaptive_join_takes_the_local_branch_when_the_keys_do_not_fit()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, stats) = await RunAsync(AdaptivePlan(maxKeys: 3), local, remote, catalog);

        var decision = Assert.Single(stats.AdaptiveDecisions);
        Assert.Equal(AdaptiveBranch.Local, decision.Branch);
        Assert.Equal(4, decision.DistinctKeys);
        Assert.Equal(6, rows.Count);

        // The local branch fetches the whole table, which is the trade the threshold decides.
        Assert.Equal(5, stats.RowsFetched);
        Assert.Empty(remote.Calls.SelectMany(k => k));
    }

    // ---------------------------------------------------------------- fan-out

    private static Plan FanOutPlan(int partitions) =>
        IrBuilder.Plan(
            IrBuilder.PartitionedScan(
                Enumerable.Range(0, partitions)
                    .Select(i => (
                        IrBuilder.RemoteQuery("far", "SELECT k, v FROM facts", Facts.RowType()),
                        (Expr?)IrBuilder.Lit("a")))));

    /// <summary>Every branch is read, and its rows come out.</summary>
    [Fact]
    public async Task A_fan_out_reads_every_partition()
    {
        var (local, remote, catalog) = Fixture();

        var (rows, stats) = await RunAsync(FanOutPlan(3), local, remote, catalog);

        Assert.Equal(3 * Facts.Rows.Count, rows.Count);
        Assert.Equal(3, remote.Calls.Count);
        Assert.Equal(3, stats.RemoteCalls);
    }

    /// <summary>
    /// The branches genuinely overlap: with a delay on every call, three partitions are in flight at
    /// once. A structural assertion on the source's own counter, not a timing.
    /// </summary>
    [Fact]
    public async Task A_fan_out_reads_its_partitions_concurrently()
    {
        var (local, remote, catalog) = Fixture();
        remote.Delay = TimeSpan.FromMilliseconds(50);

        await RunAsync(FanOutPlan(3), local, remote, catalog);

        Assert.Equal(3, remote.PeakConcurrency);
    }

    /// <summary>
    /// …unless the host says otherwise. <c>MaxConcurrentQueries</c> is the adapter author's ceiling
    /// and it is honoured however many partitions there are (D107).
    /// </summary>
    [Fact]
    public async Task A_fan_out_honours_the_per_source_concurrency_limit()
    {
        var (local, remote, catalog) = Fixture();
        remote.Delay = TimeSpan.FromMilliseconds(50);

        await RunAsync(
            FanOutPlan(4),
            local,
            remote,
            catalog,
            new ExecutionSettings
            {
                BatchSize = 4096,
                SourceOptions = new Dictionary<string, SourceOptions>(StringComparer.Ordinal)
                {
                    ["far"] = new() { MaxConcurrentQueries = 1 },
                },
            });

        Assert.Equal(1, remote.PeakConcurrency);
    }

    /// <summary>And the overall ceiling, which is the host's rather than the adapter's.</summary>
    [Fact]
    public async Task A_fan_out_honours_the_overall_concurrency_limit()
    {
        var (local, remote, catalog) = Fixture();
        remote.Delay = TimeSpan.FromMilliseconds(50);

        await RunAsync(
            FanOutPlan(4),
            local,
            remote,
            catalog,
            new ExecutionSettings { BatchSize = 4096, MaxRemoteConcurrency = 2 });

        Assert.True(
            remote.PeakConcurrency <= 2,
            $"{remote.PeakConcurrency} calls were in flight against a ceiling of 2");
    }

    // ---------------------------------------------------------------- failure and cancellation

    /// <summary>
    /// One source failing is one attributable error, and nothing partial: the enumerator throws
    /// instead of yielding (§5, exit criterion).
    /// </summary>
    [Fact]
    public async Task A_failing_partition_faults_the_query_naming_the_source()
    {
        var (local, remote, catalog) = Fixture();
        remote.FailOnCall = 2;

        var failure = await Assert.ThrowsAnyAsync<ChalkException>(
            async () => await RunAsync(FanOutPlan(3), local, remote, catalog));

        var attributed = Find<SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.Equal("far", attributed.SourceId);
    }

    /// <summary>
    /// D262: with the engine's redaction on, what the failure <em>quotes</em> is the pushed query
    /// with its literals as pseudonyms — the values are in the pushed SQL, and a host that redacts
    /// its logs and then catches one of these would otherwise have them back.
    /// </summary>
    [Fact]
    public async Task A_failing_source_quotes_the_redacted_query_when_the_engine_redacts()
    {
        var (local, remote, catalog) = Fixture();
        remote.FailOnCall = 1;
        const string Pushed = "SELECT k, v FROM facts WHERE k = 'a secret'";
        const string Redacted = "SELECT k, v FROM facts WHERE k = /*REDACTED-3f9a1c2e:CHAR*/";

        var failure = await Assert.ThrowsAnyAsync<ChalkException>(
            async () => await RunAsync(
                IrBuilder.Plan(IrBuilder.RemoteQuery("far", Pushed, Facts.RowType())),
                local,
                remote,
                catalog,
                redactedQueryText: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Pushed] = Redacted,
                }));

        var attributed = Find<SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.Equal("far", attributed.SourceId);
        Assert.Equal(Redacted, attributed.Subject);
        Assert.Contains(Redacted, attributed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a secret", attributed.Message, StringComparison.Ordinal);
        // The provider's own exception is untouched: a redaction replaces the subject and nothing
        // else, so a host loses no detail about what the source actually said.
        Assert.IsType<InvalidOperationException>(attributed.InnerException);
    }

    /// <summary>And an engine that did not ask is exactly as it was.</summary>
    [Fact]
    public async Task A_failing_source_quotes_the_query_as_written_when_the_engine_does_not_redact()
    {
        var (local, remote, catalog) = Fixture();
        remote.FailOnCall = 1;
        const string Pushed = "SELECT k, v FROM facts WHERE k = 'a secret'";

        var failure = await Assert.ThrowsAnyAsync<ChalkException>(
            async () => await RunAsync(
                IrBuilder.Plan(IrBuilder.RemoteQuery("far", Pushed, Facts.RowType())),
                local,
                remote,
                catalog));

        var attributed = Find<SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.Equal("the fake query", attributed.Subject);
    }

    /// <summary>A failing lookup call is attributed the same way.</summary>
    [Fact]
    public async Task A_failing_lookup_call_faults_the_query_naming_the_source()
    {
        var (local, remote, catalog) = Fixture();
        remote.FailOnCall = 1;

        var failure = await Assert.ThrowsAnyAsync<ChalkException>(
            async () => await RunAsync(LookupPlan(), local, remote, catalog));

        Assert.NotNull(Find<SourceExecutionException>(failure));
    }

    /// <summary>
    /// Cancellation reaches the in-flight fetches and the enumerator completes: a bound, not a
    /// benchmark (§5, D108). The source records that it saw the token.
    /// </summary>
    [Fact]
    public async Task Cancellation_reaches_every_in_flight_fetch()
    {
        var (local, remote, catalog) = Fixture();
        remote.Hangs = true;

        var compiled = PlanCompiler.Compile(
            FanOutPlan(3),
            catalog,
            new Dictionary<string, ISourceRuntime>(StringComparer.Ordinal)
            {
                [local.SourceId] = local,
                [remote.SourceId] = remote,
            },
            new ExecutionSettings
            {
                BatchSize = 4096,
                CancellationGracePeriod = TimeSpan.FromSeconds(2),
            });

        using var cancellation = new CancellationTokenSource();
        var enumerator = compiled
            .ExecuteAsync([], new ExecutionStats(), arena: null, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var pending = enumerator.MoveNextAsync().AsTask();

        // Cancel once a fetch is genuinely in flight. Cancelling before one is is a different test —
        // the token is already cancelled when the prefetch task first pulls, the enumerator faults
        // exactly as it should, and no call ever reached the source to observe anything.
        var started = await Task.WhenAny(remote.Hanging, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(remote.Hanging, started);
        await cancellation.CancelAsync();

        // A bound, not a benchmark (§5): the assertion is that the enumerator finishes at all, and
        // the ceiling is generous enough that a slow machine is never the reason it did not.
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(pending, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await enumerator.DisposeAsync();

        Assert.True(remote.ObservedCancellation, "no fetch task observed the cancellation");
    }

    /// <summary>
    /// D98, D101: there is no cross-source snapshot, so the stats say which moment each source was
    /// read at and how much came back.
    /// </summary>
    [Fact]
    public async Task Every_source_a_query_read_is_in_the_fetch_stats()
    {
        var (local, remote, catalog) = Fixture();

        var (_, stats) = await RunAsync(LookupPlan(maxKeysPerCall: 2), local, remote, catalog);

        var fetch = Assert.Contains("far", stats.SourceFetches);
        Assert.Equal(3, fetch.Calls);
        Assert.Equal(6, fetch.Rows);
        Assert.True(fetch.FirstFetch <= fetch.LastFetch);
    }

    private static T? Find<T>(Exception? failure)
        where T : Exception
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }
}
