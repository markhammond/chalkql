using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The correctness backbone (invariant I4, docs/design/05-testing.md §5). Every corpus query is run
/// two ways and the results compared:
///
/// <list type="number">
/// <item>vectorised on a <c>FULL</c> plan vs the reference executor on a <c>NONE</c> plan — the
/// canonical comparison, which catches pushdown bugs as well as operator bugs;</item>
/// <item>vectorised on <c>NONE</c> vs reference on <c>NONE</c> — the same plan through both engines,
/// which isolates operator bugs from pushdown bugs.</item>
/// </list>
///
/// <para>
/// The reference executor is an independent row-at-a-time interpreter (D13): it shares no kernels and
/// no operators with the vectorised engine, so agreement means something. Batch-boundary coverage is
/// <see cref="BatchBoundaryDifferentialTests"/>.
/// </para>
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class CorpusDifferentialTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    /// <summary>
    /// Every corpus query but the window family, which has its own class: the reference executor
    /// materialises each window frame, so it wants a fixture small enough to do that on (D53).
    /// </summary>
    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadAll())
        {
            if (query.Milestone != WindowDifferentialTests.Milestone
                && query.Milestone != WindowsIiDifferentialTests.Milestone)
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Vectorised_on_full_agrees_with_the_reference_executor_on_none(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);
        await CompareAsync(query, PushdownLevel.Full, batchSize: 4096);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Both_engines_agree_on_the_same_reference_plan(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);
        await CompareAsync(query, PushdownLevel.None, batchSize: 4096);
    }

    /// <summary>
    /// Instrumentation (05-testing.md §1, amended by ADR 0009 and by M2's D40). A plan whose leaves
    /// are all full scans reads every row of every table it touches; a plan with an
    /// <c>IndexLookup</c> reads fewer, and one whose <c>Fetch</c> is satisfied before the scan runs
    /// out reads fewer still — the pull pipeline simply stops asking. Either way a silent change
    /// means pushdown started or stopped happening without anyone noticing.
    ///
    /// <para>
    /// A query whose <c>-- expect:</c> block says <c>rows_scanned=produced</c> is a pure lookup: it
    /// must read exactly the rows it hands back, which is the M2 exit criterion and a stronger claim
    /// than any bound.
    /// </para>
    /// </summary>
    /// <summary>
    /// The rows-scanned baseline's queries: every one this class runs whose plan has no window. A
    /// window's required ordering turns a scan into an index-ordered lookup that still reads every
    /// row, so "a lookup reads fewer rows than the table has" is not true of one — which is the same
    /// reason the two window families have differential classes of their own. Decided from the
    /// recorded plan, so the theory has no skipped case.
    /// </summary>
    public static TheoryData<string> ScannedQueries()
    {
        var data = new TheoryData<string>();
        var windowFamilies = new[]
        {
            WindowDifferentialTests.Milestone, WindowsIiDifferentialTests.Milestone,
        };
        foreach (var query in CorpusQueries.LoadAll())
        {
            if (windowFamilies.Contains(query.Milestone))
            {
                continue;
            }

            var path = Path.Combine(
                RepoLayout.PlansFor(query.Milestone).FullName, $"{query.Name}.full.binpb");
            if (File.Exists(path)
                && PlanWalker.Has(Plan.Parser.ParseFrom(File.ReadAllBytes(path)), Rel.KindOneofCase.Window))
            {
                continue;
            }

            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScannedQueries))]
    public async Task A_query_scans_the_rows_the_baseline_says_it_should(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        var (batches, stats, plan) =
            await DifferentialRunner.RunWithPlanAsync(engine, prepared, query);
        var scanned = DifferentialRunner.ExpectedRowsScanned(plan, Fixture);
        long produced;
        try
        {
            produced = batches.Sum(b => (long)b.Length);
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }

        var lookup = PlanWalker.Has(plan, Rel.KindOneofCase.IndexLookup);
        var fetch = PlanWalker.Has(plan, Rel.KindOneofCase.Fetch);

        // A VALUES branch of a set operation contributes rows nothing read (D69), so for those
        // plans — and only those — the produced count stops being a lower bound on what was
        // scanned. Every other VALUES-bearing query keeps the check.
        var literalBranch = PlanWalker.Has(plan, Rel.KindOneofCase.VirtualTable)
            && PlanWalker.Has(plan, Rel.KindOneofCase.SetOp);
        var floor = literalBranch ? 0 : produced;

        // D276: a query whose leaf the planner goaled says how far the source was allowed to read,
        // which is the batch ramp asserted structurally rather than by timing. A fact about an
        // execution, like rows_scanned, so it lives here; and it is checked beside the branches
        // below rather than instead of them, so both claims have to hold.
        var atMost = query.Expectations.FirstOrDefault(
            e => e.StartsWith("rows_scanned_at_most=", StringComparison.Ordinal));
        if (atMost is not null)
        {
            var bound = long.Parse(
                atMost["rows_scanned_at_most=".Length..],
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(
                stats.RowsScanned <= bound,
                $"{name}: read {stats.RowsScanned} rows against a bound of {bound}");
        }

        if (query.Expectations.Contains("rows_scanned=produced"))
        {
            Assert.True(lookup, $"{name}: rows_scanned=produced but the plan has no IndexLookup");
            Assert.Equal(produced, stats.RowsScanned);
        }
        else if (lookup)
        {
            Assert.InRange(stats.RowsScanned, floor, scanned - 1);
        }
        else if (fetch)
        {
            Assert.InRange(stats.RowsScanned, floor, scanned);
        }
        else
        {
            Assert.Equal(scanned, stats.RowsScanned);
        }
    }

    /// <summary>
    /// The queries whose whole <c>FULL</c> plan is one single-range <c>IndexLookup</c> — the only
    /// ones whose lookup ordering is observable in the result, because nothing above it could have
    /// restored an order it failed to deliver. Read from the recorded plan at discovery time rather
    /// than decided at run time, so the suite reports no skipped tests.
    ///
    /// <para>
    /// A query with a list-valued parameter is left out: the recorded plan is the one-element form
    /// the planner sees (D27), and binding a longer list turns the one range into several — which is
    /// a union, and a union of ordered runs is not ordered.
    /// </para>
    /// </summary>
    public static TheoryData<string> SingleRangeLookupQueries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadAll())
        {
            var path = Path.Combine(
                RepoLayout.PlansFor(query.Milestone).FullName, $"{query.Name}.full.binpb");
            if (!File.Exists(path)
                || query.Expectations.Any(e => e.StartsWith("accepts_list=", StringComparison.Ordinal)))
            {
                continue;
            }

            // A lookup that claims no ordering has none to check: a HASH index answers equality in
            // whatever order it finds the rows, and a PREFIX one walks a trie (D282). The claim is
            // what this test is about, so a lookup that makes none is not one of its cases.
            var root = Plan.Parser.ParseFrom(File.ReadAllBytes(path)).Root;
            if (root.KindCase == Rel.KindOneofCase.IndexLookup
                && root.IndexLookup.Ranges.Count == 1
                && root.Collations.Count > 0)
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// The ordering an <c>IndexLookup</c> promises is the one it delivers (§5). The differential
    /// comparison already checks every <c>ORDER BY</c> key the plan claims; this checks the claim a
    /// single-range lookup makes on its own, before anything above it could have restored the order.
    /// </summary>
    [Theory]
    [MemberData(nameof(SingleRangeLookupQueries))]
    public async Task A_single_range_lookup_hands_back_rows_in_key_order(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        var (batches, _, plan) = await DifferentialRunner.RunWithPlanAsync(engine, prepared, query);
        try
        {
            // The theory data promised this shape; if the live plan disagrees, say so here rather
            // than silently checking nothing.
            Assert.Equal(Rel.KindOneofCase.IndexLookup, plan.Root.KindCase);
            Assert.Single(plan.Root.IndexLookup.Ranges);

            var keys = plan.Root.Collations.Count == 0 ? [] : plan.Root.Collations[0].Fields
                .Select(f => new OrderKeyExpectation(
                    (int)f.Expr.FieldRef.Index,
                    f.Direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast,
                    f.Direction is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst))
                .ToArray();
            Assert.NotEmpty(keys);
            ResultComparer.AssertOrdered(batches, keys);
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }
    }

    /// <summary>
    /// V52, ADR 0024: <c>MOD</c> takes its declared type from its <em>second</em> argument, so
    /// <c>MOD(BIGINT, INTEGER)</c> is an INTEGER while I-IR-2 widens both operands to BIGINT — and
    /// the vectorised executor closes its kernel over the call's declared type. It therefore read a
    /// 64-bit column, and a 64-bit literal, as 32-bit lanes.
    /// </summary>
    /// <remarks>
    /// The planner's <c>RelToIrTest</c> pins the IR shape; this pins the answer, over a dividend that
    /// does not fit in 32 bits, against the engine that shares no kernels with the one under test.
    /// The literal is what made the failure loud rather than quiet — its high half is zero, so every
    /// other lane divided by zero — but a dividend the low half cannot hold is what makes this a
    /// value test and not a crash test.
    /// </remarks>
    [Fact]
    public async Task A_modulus_over_a_dividend_wider_than_its_divisor_agrees_with_the_reference()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        // volume is a BIGINT in the thousands; scaled by 2^32 it is far outside a 32-bit lane, and
        // MOD 7 of it differs from MOD 7 of its low half for every row.
        const string Sql = """
            SELECT symbol, MOD(volume * 4294967296, 7) AS m, volume * 4294967296 AS wide
            FROM bars
            ORDER BY symbol, ts
            """;

        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference, 4096);

        var actual = await BatchesAsync(vectorised, Sql);
        var expected = await BatchesAsync(reference, Sql);
        try
        {
            // The test only means something if the dividend really is outside 32 bits.
            var wide = (Int64Array)expected[0].Column(2);
            Assert.True(
                wide.GetValue(0) is long v && v > int.MaxValue,
                $"the dividend must not fit in 32 bits; it was {wide.GetValue(0)}");

            Compare(
                "MOD(BIGINT, INTEGER): vectorised vs reference, both at NONE",
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

    private static async Task<List<RecordBatch>> BatchesAsync(ChalkEngine engine, string sql)
    {
        var batches = new List<RecordBatch>();
        await foreach (var batch in engine.QueryAsync(
            sql, (IReadOnlyList<object?>?)null, arena: null, TestContext.Current.CancellationToken))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>
    /// The arena contract over the whole corpus (08-execution-arena.md §3): one host-owned arena
    /// serves every query in turn, and each one leaves it empty. A rental an operator or a source
    /// forgot to return shows up here as a non-zero balance, on whichever query forgot it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task One_host_arena_serves_every_query_and_is_empty_after_each(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised, 4096);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        var (batches, stats) = await DifferentialRunner.RunAsync(engine, prepared, query, HostArena);
        try
        {
            // Touching every batch keeps the run honest: the arena's memory has to still be readable.
            Assert.Equal(stats.RowsProduced, batches.Sum(b => (long)b.Length));
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }

        Assert.Equal(0, HostArena.OutstandingBytes);
        Assert.True(
            HostArena.RetainedBytes <= HostArena.Options.RetainBytes,
            $"{name} left {HostArena.RetainedBytes} bytes retained against a bound of "
            + $"{HostArena.Options.RetainBytes}.");
    }

    /// <summary>
    /// One arena for the whole corpus. Safe as a static because every test in
    /// <see cref="SidecarCollection"/> runs in turn, which is also the arena's own rule.
    /// </summary>
    private static readonly ExecutionArena HostArena =
        new(new ArenaOptions { RetainBytes = 8 << 20 });

    private async Task CompareAsync(CorpusQuery query, PushdownLevel actualLevel, int batchSize)
    {
        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised, batchSize);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference, batchSize);

        var actualPrepared = await vectorised.PrepareAsync(
            query.Sql, query.PrepareOptions(actualLevel));
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            Compare(
                $"{query.Name}: vectorised@{actualLevel} vs reference@NONE at batch size {batchSize}",
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

    /// <summary>
    /// <see cref="ResultComparer"/> reports the row and column that differ; this adds which of the
    /// several comparisons a query goes through was the one that failed.
    /// </summary>
    internal static void Compare(
        string description,
        IReadOnlyList<RecordBatch> expected,
        IReadOnlyList<RecordBatch> actual,
        ResultComparisonOptions options)
    {
        try
        {
            ResultComparer.AssertEquivalent(expected, actual, options);
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            Assert.Fail($"{description}\n{failure.Message}");
        }
    }

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine, int batchSize) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine, BatchSize = batchSize },
        });
}

/// <summary>
/// The same comparison at batch sizes 1 and 7, over a small fixture. Batch-boundary bugs — an
/// operator that only works when a batch is full, or a group that spans two batches — show up just as
/// clearly at three hundred rows as at a hundred thousand, and running the whole corpus one row per
/// batch over the full fixture would take hours to say the same thing.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class BatchBoundaryDifferentialTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Small =
        CorpusFixture.Create(barMinutes: 61, lineItemRows: 293);

    public static TheoryData<string, int> QueriesAndBatchSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var query in CorpusQueries.Load())
        {
            data.Add(query.Name, 1);
            data.Add(query.Name, 7);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(QueriesAndBatchSizes))]
    public async Task Both_engines_agree_at_small_batch_sizes(string name, int batchSize)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);

        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised, batchSize);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference, batchSize);

        var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions());
        var expectedPrepared = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, actualPlan) =
            await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
        var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

        try
        {
            CorpusDifferentialTests.Compare(
                $"{query.Name} at batch size {batchSize}",
                expected,
                actual,
                DifferentialRunner.ComparisonFor(actualPlan, Small.Catalog));

            // Every batch except the last is full; a source that ignores BatchSize would pass the
            // value comparison and fail here.
            foreach (var batch in actual.Take(Math.Max(actual.Count - 1, 0)))
            {
                Assert.True(
                    batch.Length <= batchSize,
                    $"{query.Name}: a batch of {batch.Length} rows exceeds BatchSize {batchSize}");
            }
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine, int batchSize) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Small.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine, BatchSize = batchSize },
        });
}
