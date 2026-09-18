using Apache.Arrow;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.Sources;
using Chalk.TestKit;
using Chalk.Tests;
using CatalogContext = Chalk.Catalog.CatalogContext;
using IrField = Chalk.Ir.Field;

namespace Chalk.Execution.Tests;

/// <summary>
/// The arena of <c>docs/design/08-execution-arena.md</c> §2, and the promises it makes to a host:
/// accounting that adds up, size classes, an optional budget that names the operator that broke it,
/// a retention bound, and one execution at a time.
/// </summary>
public sealed class ArenaTests
{
    private sealed record Bar(string Symbol, long Volume, double Close);

    private const int Rows = 20_000;

    private readonly Xunit.ITestOutputHelper _output;

    public ArenaTests(Xunit.ITestOutputHelper output) => _output = output;

    [Fact]
    public void Rent_and_return_balance_and_the_peak_is_the_high_water_mark()
    {
        using var arena = new ExecutionArena();

        var first = arena.Rent<int>(1000);
        Assert.Equal(4096, arena.OutstandingBytes);

        var second = arena.Rent<long>(300);
        Assert.Equal(4096 + 4096, arena.OutstandingBytes);

        arena.Return(second);
        arena.Return(first);

        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(8192, arena.PeakBytes);
        Assert.Equal(8192, arena.RetainedBytes);
    }

    /// <summary>Powers of two from 256 bytes, and the same class hands the same array back (§2.3).</summary>
    [Theory]
    [InlineData(1, 256)]
    [InlineData(200, 256)]
    [InlineData(257, 512)]
    [InlineData(4096, 4096)]
    [InlineData(4097, 8192)]
    public void A_rental_is_rounded_up_to_a_power_of_two_size_class(int asked, int expected)
    {
        using var arena = new ExecutionArena();

        var array = arena.Rent<byte>(asked);

        Assert.Equal(expected, array.Length);
        Assert.Equal(expected, arena.OutstandingBytes);
        arena.Return(array);
    }

    [Fact]
    public void A_returned_array_is_handed_out_again_rather_than_reallocated()
    {
        using var arena = new ExecutionArena();
        var first = arena.Rent<int>(512);
        arena.Return(first);

        var second = arena.Rent<int>(512);

        Assert.Same(first, second);
        Assert.Equal(0, arena.RetainedBytes);
        arena.Return(second);
    }

    /// <summary>
    /// D258.1: there is no pooling ceiling. A rental well past the 64 MiB the arena used to refuse is
    /// a size class like any other, so the next execution of the same shape rents what the last one
    /// returned instead of allocating it again.
    /// </summary>
    [Fact]
    public void A_rental_above_the_old_sixty_four_megabyte_ceiling_is_pooled_and_served_again()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 256L << 20 });
        const int overCeiling = (64 << 20) + 1;

        var first = arena.Rent<byte>(overCeiling);

        Assert.Equal(128 << 20, first.Length);
        Assert.Equal(1, arena.HeapFallbacks);

        arena.Return(first);
        Assert.Equal(128L << 20, arena.RetainedBytes);
        Assert.Equal(0, arena.UnpooledReturnBytes);

        var second = arena.Rent<byte>(overCeiling);

        Assert.Same(first, second);
        Assert.Equal(1, arena.HeapFallbacks);
        Assert.Equal(0, arena.RetainedBytes);
        arena.Return(second);
    }

    /// <summary>
    /// D258.1: a host that wants the old behaviour asks for it. Above
    /// <see cref="ArenaOptions.MaxPooledRental"/> a rental is served exactly — no rounding up to the
    /// next class — and refused on the way back, which is what <c>UnpooledReturnBytes</c> counts.
    /// </summary>
    [Fact]
    public void MaxPooledRental_caps_what_the_pools_keep_and_the_refusal_is_counted()
    {
        using var arena = new ExecutionArena(new ArenaOptions { MaxPooledRental = 64 << 10 });

        var under = arena.Rent<byte>((64 << 10) - 1);
        Assert.Equal(64 << 10, under.Length);
        arena.Return(under);
        Assert.Equal(64L << 10, arena.RetainedBytes);
        Assert.Equal(0, arena.UnpooledReturnBytes);

        var over = arena.Rent<byte>((64 << 10) + 1);

        Assert.Equal((64 << 10) + 1, over.Length);
        Assert.NotSame(under, over);
        arena.Return(over);

        Assert.Equal((64L << 10) + 1, arena.UnpooledReturnBytes);
        Assert.Equal(64L << 10, arena.RetainedBytes);
    }

    [Fact]
    public void MaxPooledRental_must_be_positive_when_it_is_set()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExecutionArena(new ArenaOptions { MaxPooledRental = 0 }));
    }

    /// <summary>
    /// The counters of ADR 0035: a rental the pools cannot serve is the arena's own fall back to the
    /// heap, and a warm pool serves the same rental again without one.
    /// </summary>
    [Fact]
    public void A_rental_the_pool_cannot_serve_is_counted_as_a_heap_fallback()
    {
        using var arena = new ExecutionArena();

        var cold = arena.Rent<int>(512);

        Assert.Equal(1, arena.Rentals);
        Assert.Equal(512 * sizeof(int), arena.RentedBytes);
        Assert.Equal(1, arena.HeapFallbacks);
        Assert.Equal(512 * sizeof(int), arena.HeapFallbackBytes);

        arena.Return(cold);
        var warm = arena.Rent<int>(512);

        Assert.Same(cold, warm);
        Assert.Equal(2, arena.Rentals);
        Assert.Equal(1, arena.HeapFallbacks);
        Assert.Equal(0, arena.UnpooledReturnBytes);
        arena.Return(warm);
    }

    /// <summary>
    /// The two ways a byte leaves the arena for the collector, told apart: above a host's
    /// <see cref="ArenaOptions.MaxPooledRental"/> it is refused on the way back, and under it the trim
    /// lets it go. This is the split <c>33-aggregate-performance.md</c> §3.3 read the window query's
    /// memory with, and since D258.1 the first of the two happens only where a host asked for it.
    /// </summary>
    [Fact]
    public void The_pooling_cap_and_the_trim_are_counted_apart()
    {
        using var arena = new ExecutionArena(
            new ArenaOptions { RetainBytes = 0, MaxPooledRental = 64 << 10 });
        const int cap = 64 << 10;

        arena.Return(arena.Rent<byte>(cap + 1));

        Assert.Equal(cap + 1, arena.UnpooledReturnBytes);
        Assert.Equal(0, arena.TrimmedBytes);

        arena.Return(arena.Rent<byte>(4096));
        arena.Trim();

        Assert.Equal(cap + 1, arena.UnpooledReturnBytes);
        Assert.Equal(4096, arena.TrimmedBytes);
    }

    [Fact]
    public void String_staging_comes_back_cleared_so_the_pool_pins_nothing()
    {
        using var arena = new ExecutionArena();
        var staging = arena.RentStrings(64);
        staging[0] = "held";

        arena.ReturnStrings(staging);
        var again = arena.RentStrings(64);

        Assert.Same(staging, again);
        Assert.Null(again[0]);
        arena.ReturnStrings(again);
    }

    [Fact]
    public void Trim_keeps_RetainBytes_and_no_more()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 8192 });
        var arrays = new byte[8][];
        for (var i = 0; i < arrays.Length; i++)
        {
            arrays[i] = arena.Rent<byte>(4096);
        }

        foreach (var array in arrays)
        {
            arena.Return(array);
        }

        Assert.Equal(8 * 4096, arena.RetainedBytes);

        arena.Trim();

        Assert.True(
            arena.RetainedBytes <= 8192,
            $"Trim left {arena.RetainedBytes} bytes retained against a RetainBytes of 8192.");
    }

    /// <summary>
    /// D258.2: under a budget that fits exactly one of two classes, the one rented last survives and
    /// the other goes — whichever of them is the larger. The old rule dropped the largest class
    /// first, which is the one a blocking operator is about to ask for again.
    /// </summary>
    [Fact]
    public void Trim_evicts_the_least_recently_used_class_and_keeps_the_most_recent()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 8192 });

        // Rent the big class first, then the small one, and return them in the same order, so that
        // "last returned" and "last rented" both name the small one.
        var big = arena.Rent<byte>(8192);
        var small = arena.Rent<byte>(4096);
        arena.Return(big);
        arena.Return(small);
        Assert.Equal(8192 + 4096, arena.RetainedBytes);

        arena.Trim();

        // 8192 + 4096 is over the budget by 4096, so exactly one class goes: the older, larger one.
        Assert.Equal(4096, arena.RetainedBytes);
        Assert.Equal(8192, arena.TrimmedBytes);
        Assert.Same(small, arena.Rent<byte>(4096));
    }

    /// <summary>
    /// The mirror image: rent the large class last and it is the one that survives, which is the
    /// case the largest-first rule could never serve (D258.2).
    /// </summary>
    [Fact]
    public void Trim_keeps_the_largest_class_when_it_is_the_one_last_rented()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 8192 });

        var small = arena.Rent<byte>(4096);
        var big = arena.Rent<byte>(8192);
        arena.Return(small);
        arena.Return(big);

        arena.Trim();

        Assert.Equal(8192, arena.RetainedBytes);
        Assert.Equal(4096, arena.TrimmedBytes);
        Assert.Same(big, arena.Rent<byte>(8192));
    }

    /// <summary>
    /// A class is used when it is <em>rented</em>, not when it is returned (D258.2). Here the two
    /// orders disagree: the 8 KiB class is rented first and returned last, and it is the rental that
    /// decides.
    /// </summary>
    [Fact]
    public void A_class_counts_as_used_when_it_is_rented_rather_than_when_it_is_returned()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 8192 });

        var rentedFirst = arena.Rent<byte>(8192);
        var rentedLast = arena.Rent<byte>(4096);
        arena.Return(rentedLast);
        arena.Return(rentedFirst);

        arena.Trim();

        Assert.Equal(4096, arena.RetainedBytes);
        Assert.Equal(8192, arena.TrimmedBytes);
        Assert.Same(rentedLast, arena.Rent<byte>(4096));
    }

    /// <summary>
    /// The counters the trim touches agree with each other and with what is left: what came back
    /// minus what the trim released is what the pools still hold, and the classes that survive are
    /// the ones rented most recently.
    /// </summary>
    [Fact]
    public void The_trim_counters_and_the_retained_bytes_agree()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 65536 });

        long returned = 0;
        for (var length = 4096; length <= 65536; length <<= 1)
        {
            var array = arena.Rent<byte>(length);
            returned += array.Length;
            arena.Return(array);
        }

        Assert.Equal(returned, arena.RetainedBytes);
        Assert.Equal(0, arena.TrimmedBytes);

        arena.Trim();

        Assert.Equal(0, arena.UnpooledReturnBytes);
        Assert.Equal(returned - arena.RetainedBytes, arena.TrimmedBytes);

        // Rented smallest first, so the 64 KiB class is the most recent — and the only one a 64 KiB
        // budget has room for.
        Assert.Equal(65536, arena.RetainedBytes);
        Assert.Equal(returned - 65536, arena.TrimmedBytes);
    }

    [Fact]
    public void Trim_does_not_touch_what_is_still_outstanding()
    {
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 0 });
        var live = arena.Rent<byte>(4096);
        live[0] = 42;

        arena.Trim();

        Assert.Equal(4096, arena.OutstandingBytes);
        Assert.Equal(42, live[0]);

        // Returning pools it; only the next Trim — or the end of an execution — lets it go.
        arena.Return(live);
        Assert.Equal(4096, arena.RetainedBytes);
        arena.Trim();
        Assert.Equal(0, arena.RetainedBytes);
    }

    [Fact]
    public void Dispose_releases_everything_and_refuses_further_rentals()
    {
        var arena = new ExecutionArena();
        arena.Return(arena.Rent<byte>(4096));
        Assert.Equal(4096, arena.RetainedBytes);

        arena.Dispose();

        Assert.Equal(0, arena.RetainedBytes);
        Assert.Throws<ObjectDisposedException>(() => arena.Rent<byte>(16));
        arena.Dispose();
    }

    [Fact]
    public void A_second_concurrent_execution_on_one_arena_throws()
    {
        using var arena = new ExecutionArena();
        arena.BeginExecution();

        var error = Assert.Throws<InvalidOperationException>(() => arena.BeginExecution());

        Assert.Contains("one execution at a time", error.Message, StringComparison.Ordinal);
        arena.EndExecution();
        arena.BeginExecution();
        arena.EndExecution();
    }

    /// <summary>
    /// Two executions started against the same arena — the shape a host hits by handing one arena to
    /// two pipelines — are refused at the first batch rather than corrupting each other.
    /// </summary>
    [Fact]
    public async Task A_second_execution_on_a_busy_arena_is_refused_by_the_engine()
    {
        var compiled = Pipeline(out _);
        using var arena = new ExecutionArena();

        var first = compiled.ExecuteAsync([], new ExecutionStats(), arena, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await first.MoveNextAsync());
            first.Current.Dispose();

            var second = compiled
                .ExecuteAsync([], new ExecutionStats(), arena, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
            await second.DisposeAsync();
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    /// <summary>
    /// The first per-query memory limit Chalk has had. The failure names the budget and the operator
    /// that asked for it, because <c>OperatorBase</c> wraps it on the way out (§2).
    /// </summary>
    [Fact]
    public async Task An_execution_that_breaks_MaxBytes_fails_naming_the_budget_and_the_operator()
    {
        var compiled = Pipeline(out _);
        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 4096 });

        var error = await Assert.ThrowsAsync<ExecutionException>(async () =>
        {
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        _output.WriteLine(error.Message);
        Assert.Contains("arena budget of 4096 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains(error.OperatorPath, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("root", error.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(error.InnerException);

        // A refused rental is not a charged one: the arena is intact for whatever comes next.
        Assert.Equal(0, arena.OutstandingBytes);
    }

    [Fact]
    public async Task An_unbounded_arena_is_the_default_and_runs_the_same_plan()
    {
        var compiled = Pipeline(out var expected);
        using var arena = new ExecutionArena();

        Assert.Null(arena.Options.MaxBytes);
        Assert.Equal(64L << 20, arena.Options.RetainBytes);
        Assert.Equal(expected, await ConsumeAsync(compiled, arena));
    }

    /// <summary>
    /// The leak test of §3: a thousand executions on one arena. If anything an operator or a source
    /// rents were kept, the arena would have to allocate a fresh array in its place on the next run,
    /// so its high-water mark would climb and so would the managed bytes each execution allocates.
    /// </summary>
    /// <remarks>
    /// The measure is <see cref="GC.GetAllocatedBytesForCurrentThread"/> rather than the process-wide
    /// managed heap: the pipeline is synchronous and runs entirely on the calling thread, so the delta
    /// is this execution's allocation and not the allocation of whatever is running beside it. The
    /// process-wide heap could not say that — it drifts by megabytes under xunit's parallel run.
    /// A test running beside this one can still perturb the per-thread count, by triggering a
    /// collection inside a measured window; every window here is therefore taken through
    /// <see cref="AllocationProbe"/>, which measures an execution again rather than believe a reading
    /// something disturbed. That matters more here than anywhere: this gate reads a thousand windows
    /// and asserts on the largest, so one perturbed window decides it on its own.
    /// </remarks>
    [Fact]
    public async Task A_thousand_executions_on_one_arena_stay_flat()
    {
        // Pooled output, so every buffer in the run — the source's, the operators', the batches the
        // host disposes — is the arena's and nothing else is expected to be allocated at all.
        var compiled = Pipeline(out var expected, pooledOutput: true);
        using var arena = new ExecutionArena();

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(expected, await ConsumeAsync(compiled, arena));
        }

        var warmPeak = arena.PeakBytes;
        var warmRetained = arena.RetainedBytes;

        // A warm execution's allocation is the batch objects and the enumerator machinery, and
        // nothing else: every buffer comes from the arena. Anything the arena had to allocate afresh
        // — because the last run kept what it rented — would land on top of this number.
        const int Executions = 1_000;
        var perExecution = new long[Executions];
        for (var i = 0; i < Executions; i++)
        {
            (perExecution[i], _) = await AllocationProbe.OnceAsync(
                () => ConsumeAsync(compiled, arena));
            Assert.Equal(0, arena.OutstandingBytes);
        }

        var total = perExecution.Sum();
        var max = perExecution.Max();
        var firstHalf = perExecution.Take(Executions / 2).Sum();
        var secondHalf = perExecution.Skip(Executions / 2).Sum();

        // The creep test compares the *floor* of each half rather than its sum (F85). A collection
        // inside a measured window charges the thread for the unconsumed tail of its allocation
        // context — up to about 8 KB — and can only ever add, never subtract (`AllocationProbe`);
        // over five hundred windows a sum accumulates that noise, and under a loaded machine the two
        // sums drifted by about one per cent with peak and retained bytes identical, which is the
        // flake this was registered for. A minimum cannot accumulate it: the least-disturbed reading
        // of five hundred is the steady-state cost, and a rental this run leaked would raise it on
        // every execution of the second half rather than on the noisy ones. The budget below is
        // unchanged — the remedy is the statistic, never the threshold.
        var firstFloor = perExecution.Take(Executions / 2).Min();
        var secondFloor = perExecution.Skip(Executions / 2).Min();
        _output.WriteLine(
            $"peak {warmPeak} -> {arena.PeakBytes} bytes; retained {warmRetained} -> {arena.RetainedBytes}; "
            + $"allocated {total} bytes over {Executions} executions "
            + $"(mean {total / (double)Executions:F0}, max {max}, "
            + $"first half {firstHalf}, second half {secondHalf}, "
            + $"floors {firstFloor} and {secondFloor}).");

        Assert.Equal(warmPeak, arena.PeakBytes);
        Assert.Equal(warmRetained, arena.RetainedBytes);

        // One byte per input row, the milestone's own gate. The smallest array this pipeline rents is
        // 4 KiB, so a single leaked rental — re-allocated on every run because the last one kept it —
        // puts an execution over this budget on its own.
        const long PerExecutionBudget = Rows;
        Assert.True(
            max <= PerExecutionBudget,
            $"an execution allocated {max} managed bytes, over the {PerExecutionBudget}-byte budget; "
            + $"the mean was {total / (double)Executions:F0}.");

        // And it does not creep: the cheapest of the second five hundred executions costs what the
        // cheapest of the first five hundred did.
        Assert.True(
            secondFloor <= firstFloor + PerExecutionBudget,
            $"allocation per execution grew: the cheapest of the first {Executions / 2} executions "
            + $"allocated {firstFloor} bytes and the cheapest of the second {secondFloor} "
            + $"(sums {firstHalf} and {secondHalf}).");
    }

    /// <summary>
    /// The aggregate's per-group state and the sort's permutation scale with the data, not with the
    /// plan, so both belong to the arena too (ADR 0012). A second run over the same warm tree starts
    /// from an empty arena and gets the same answer.
    /// </summary>
    [Fact]
    public async Task Aggregate_and_sort_state_goes_back_to_the_arena_between_runs()
    {
        var table = TestData.Series(5_000);
        var source = TestData.Source(table);
        var row = table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Sort(
            IrBuilder.HashAggregate(
                IrBuilder.Read(table.Name, row),
                [2],
                [
                    ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                    ("total", IrBuilder.Agg(
                        AggregateFunctionId.Sum, IrBuilder.I64(), IrBuilder.Ref(row, 0))),
                    ("worst", IrBuilder.Agg(
                        AggregateFunctionId.Min, IrBuilder.Str(), IrBuilder.Ref(row, 2))),
                ]),
            IrBuilder.Asc(0, IrBuilder.Str())));

        var compiled = Runner.Compile(plan, source);
        using var arena = new ExecutionArena();

        var first = await ConsumeAsync(compiled, arena);
        Assert.Equal(0, arena.OutstandingBytes);
        var peak = arena.PeakBytes;

        var second = await ConsumeAsync(compiled, arena);

        Assert.Equal(5_000, first);
        Assert.Equal(first, second);
        Assert.Equal(0, arena.OutstandingBytes);
        Assert.Equal(peak, arena.PeakBytes);
    }

    /// <summary>
    /// D258.3: a blocking operator told how many rows are coming asks its copiers for that capacity
    /// up front, so each buffer is rented once at the size it needs instead of climbing a ladder of
    /// doubling rentals to reach it. The counters of ADR 0035 are what says so.
    /// </summary>
    [Fact]
    public async Task A_sort_given_the_plan_s_row_estimate_rents_each_buffer_once()
    {
        const int SortedRows = 100_000;

        var blind = await SortCountersAsync(SortedRows, estimate: 0);
        var told = await SortCountersAsync(SortedRows, estimate: SortedRows);

        _output.WriteLine($"no estimate: {blind}");
        _output.WriteLine($"estimate {SortedRows}: {told}");

        // Same sort, same answer: an estimate is a size and never a semantic.
        Assert.Equal(SortedRows, blind.Rows);
        Assert.Equal(blind.Rows, told.Rows);

        // The ladder is gone: fewer rentals, and fewer bytes over them.
        Assert.True(
            told.Rentals < blind.Rentals,
            $"an estimate took {told.Rentals} rentals against {blind.Rentals} without one.");
        Assert.True(
            told.RentedBytes < blind.RentedBytes,
            $"an estimate rented {told.RentedBytes} bytes against {blind.RentedBytes} without one.");

        // Every rung below the top one is pure ladder, and together they come to about what the top
        // rung costs; so the saving is at least the largest buffer this sort holds, which is its
        // hundred thousand sixteen-byte string views.
        const long LargestBuffer = SortedRows * 16L;
        Assert.True(
            blind.RentedBytes - told.RentedBytes >= LargestBuffer,
            $"the estimate saved {blind.RentedBytes - told.RentedBytes} bytes, which is less than the "
            + $"{LargestBuffer} bytes of the largest buffer the ladder had to climb to.");

        // And it never costs more memory at once: growing holds the old array and the new one
        // together, and a buffer rented at its final size never does.
        Assert.True(
            told.PeakBytes <= blind.PeakBytes,
            $"an estimate peaked at {told.PeakBytes} bytes against {blind.PeakBytes} without one.");
    }

    /// <summary>
    /// The other half of D258.3: an estimate the first batch already covers — and, by the same
    /// arithmetic, an absent or zero one — changes nothing at all. The counters are equal to the
    /// byte, which is what "keeps today's behaviour" has to mean.
    /// </summary>
    [Fact]
    public async Task An_estimate_smaller_than_the_first_batch_changes_nothing()
    {
        const int SortedRows = 20_000;

        var blind = await SortCountersAsync(SortedRows, estimate: 0);
        var tiny = await SortCountersAsync(SortedRows, estimate: 1);

        Assert.Equal(blind.Rows, tiny.Rows);
        Assert.Equal(blind.Rentals, tiny.Rentals);
        Assert.Equal(blind.RentedBytes, tiny.RentedBytes);
        Assert.Equal(blind.PeakBytes, tiny.PeakBytes);
    }

    /// <summary>
    /// An estimate is a first size and never an answer: one wildly too large and one wildly too small
    /// sort the same rows into the same order (D258.3).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5_000)]
    [InlineData(50_000_000)]
    public async Task A_wrong_estimate_is_a_wrong_first_size_and_not_a_wrong_answer(double estimate)
    {
        var table = TestData.Series(5_000);
        var source = TestData.Source(table);
        var row = table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Sort(
            IrBuilder.Read(table.Name, row, rows: estimate), IrBuilder.Asc(0, IrBuilder.I64())));

        var batches = await Runner.RunAsync(plan, source);
        try
        {
            var values = batches
                .SelectMany(batch => Enumerable
                    .Range(0, batch.Length)
                    .Select(i => ((Int64Array)batch.Column(0)).GetValue(i)!.Value))
                .ToList();

            Assert.Equal(5_000, values.Count);
            Assert.Equal(Enumerable.Range(0, 5_000).Select(i => (long)i), values);
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }
    }

    private readonly record struct SortCounters(
        int Rows, long Rentals, long RentedBytes, long PeakBytes)
    {
        public override string ToString() =>
            $"{Rows} rows, {Rentals} rentals over {RentedBytes} bytes, peak {PeakBytes}";
    }

    /// <summary>
    /// One execution of a three-column blocking sort on a cold arena of its own, and what the arena
    /// did for it. Cold, because a warm pool would serve the ladder's rungs from the last run and
    /// hide the very thing this is measuring.
    /// </summary>
    private static async Task<SortCounters> SortCountersAsync(int rows, double estimate)
    {
        var table = TestData.Series(rows);
        var source = TestData.Source(table);
        var row = table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Sort(
            IrBuilder.Read(table.Name, row, rows: estimate), IrBuilder.Asc(0, IrBuilder.I64())));

        var compiled = Runner.Compile(plan, source);
        using var arena = new ExecutionArena();
        var produced = await ConsumeAsync(compiled, arena);
        return new SortCounters(produced, arena.Rentals, arena.RentedBytes, arena.PeakBytes);
    }

    /// <summary>
    /// Under <c>OutputMemory.Pooled</c> the batches a host receives are the arena's memory: they are
    /// outstanding until the host disposes them, and they are gone once the arena is (§2.2).
    /// </summary>
    [Fact]
    public async Task Pooled_output_batches_are_arena_memory_that_the_host_must_dispose_first()
    {
        var compiled = Pipeline(out _, pooledOutput: true);
        var arena = new ExecutionArena();
        var batches = new List<RecordBatch>();

        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            batches.Add(batch);
        }

        // Held batches are held arena memory — this is exactly what OutputMemory.Managed copies away.
        Assert.True(
            arena.OutstandingBytes > 0,
            "a pooled output batch should still be outstanding against the arena that built it.");

        foreach (var batch in batches)
        {
            batch.Dispose();
        }

        Assert.Equal(0, arena.OutstandingBytes);
        Assert.True(arena.RetainedBytes > 0);

        arena.Dispose();
        Assert.Equal(0, arena.RetainedBytes);
    }

    private static async Task<int> ConsumeAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        var rows = 0;
        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    /// <summary>The benchmark's shape — read, filter, project — over a POCO source.</summary>
    private static CompiledPlan Pipeline(out int expectedRows, bool pooledOutput = false)
    {
        var bars = new Bar[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bars[i] = new Bar(i % 3 == 0 ? "AAA" : "BBB", i, i * 1.5d);
        }

        expectedRows = Rows - ((Rows + 2) / 3);
        var source = new PocoSourceBuilder("mem").AddTable("bars", bars).Build();
        var schema = source.DescribeSchema();
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] };

        var row = new RowType();
        foreach (var column in schema.Tables[0].Columns)
        {
            row.Fields.Add(new IrField { Name = column.Name, Type = column.Type.ToProto() });
        }

        var read = IrBuilder.Read("bars", row);
        var filter = IrBuilder.Filter(
            read,
            IrBuilder.Call(FunctionId.Ne, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit("AAA")));
        var project = IrBuilder.Project(
            filter,
            [
                ("symbol", IrBuilder.Ref(row, 0)),
                ("volume", IrBuilder.Ref(row, 1)),
                ("scaled", IrBuilder.Call(
                    FunctionId.Multiply, IrBuilder.Fp64(), IrBuilder.Ref(row, 2), IrBuilder.Lit(2d))),
            ]);

        return PlanCompiler.Compile(
            IrBuilder.Plan(project),
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 4096, PooledOutput = pooledOutput });
    }
}
