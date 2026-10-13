using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A standing view kept by incremental maintenance (D343, <c>docs/design/68-incremental-maintenance.md</c>
/// §7) as a host uses one: asked for with <see cref="ViewMaintenance.Incremental"/>, refused by name when
/// its statement cannot be kept so, counting what it keeps against its budget, and starting again from
/// the whole result after an update that failed.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StandingViewMaintenanceTests(SharedSidecar sidecar)
{
    private sealed record Fill(int Seq, int Book, long Qty, DateTime Ts);

    private sealed record Rate(string Symbol, DateTime Ts, double Usd);

    private struct BookKey
    {
        public long Book;
    }

    private struct BookRow
    {
        public long Qty;
    }

    private const string Totals = "SELECT book, SUM(qty) AS qty FROM fills GROUP BY book";

    private static readonly DateTime Open = new(2026, 10, 13, 9, 0, 0);

    private static readonly Fill[] FirstFills =
    [
        new(1, 1, 10, Open),
        new(2, 2, 20, Open.AddSeconds(1)),
        new(3, 3, 30, Open.AddSeconds(2)),
    ];

    /// <summary>A maintained view says so, and a drain after an append sends the one key it changed.</summary>
    [Fact]
    public async Task A_maintained_view_sends_only_what_changed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options(ViewMaintenance.Incremental), Ct);
        Assert.Equal(ViewMaintenance.Incremental, view.Maintenance);
        var replica = new Replica();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, replica);
        await AssertIsTheStatementAsync(desk.Engine, replica);

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(new Fill(4, 2, 5, Open.AddSeconds(3)))), Ct);
        await view.UpdateAsync(Ct);
        replica.Calls.Clear();
        view.Drain(ref cursor, replica);
        Assert.Equal(["upsert 2=25", $"commit {view.Version}"], replica.Calls);
        await AssertIsTheStatementAsync(desk.Engine, replica);
    }

    /// <summary>A view that recomputes keeps nothing but its rows; a maintained one counts what its operators keep.</summary>
    [Fact]
    public async Task What_maintenance_keeps_is_counted_in_the_views_bytes()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var recomputed = await desk.Engine.MaterializeAsync(Totals, Options(ViewMaintenance.Recompute), Ct);
        await using var maintained = await desk.Engine.MaterializeAsync(Totals, Options(ViewMaintenance.Incremental), Ct);
        Assert.Equal(ViewMaintenance.Recompute, recomputed.Maintenance);
        Assert.True(
            maintained.StateBytes > recomputed.StateBytes,
            $"maintained {maintained.StateBytes} bytes, recomputed {recomputed.StateBytes}");
    }

    /// <summary>A budget the maintained state does not fit is refused as a recompute's rows past it are.</summary>
    [Fact]
    public async Task A_budget_the_maintained_state_exceeds_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var recomputed = await desk.Engine.MaterializeAsync(Totals, Options(ViewMaintenance.Recompute), Ct);
        var fault = await Assert.ThrowsAsync<StandingViewException>(async () =>
            await desk.Engine.MaterializeAsync(
                Totals,
                Options(ViewMaintenance.Incremental, budget: recomputed.StateBytes + 1),
                Ct));
        Assert.Equal(ChalkErrorCodes.MemoryBudgetExceeded, fault.Code);
        Assert.Contains("kept by its incremental maintenance", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each statement maintenance cannot keep is refused when the view is made, naming what and where,
    /// and saying a recomputed view can take it.
    /// </summary>
    [Theory]
    [InlineData("SELECT seq, SUM(qty) OVER (PARTITION BY book) AS total FROM fills", "seq", "window function")]
    [InlineData(
        "SELECT book, PERCENTILE_DISC(0.5) WITHIN GROUP (ORDER BY qty) AS middle FROM fills GROUP BY book",
        "book",
        "PERCENTILE_DISC")]
    [InlineData(
        "SELECT a.seq, b.seq AS other FROM fills a FULL JOIN fills b ON a.book = b.book + 1",
        "seq",
        "FULL join")]
    public async Task A_statement_maintenance_cannot_keep_is_refused_by_name(string sql, string key, string says)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        var fault = await Assert.ThrowsAsync<StandingViewException>(async () =>
            await desk.Engine.MaterializeAsync(sql, new ViewOptions<BookKey, BookRow>
            {
                Key = [key],
                LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
                Maintenance = ViewMaintenance.Incremental,
                Map = static (in ViewRow row, out BookKey k, out BookRow value) =>
                {
                    k = new BookKey { Book = row.GetInt32(0) };
                    value = default;
                },
            }, Ct));
        Assert.Equal(ChalkErrorCodes.UnsupportedInView, fault.Code);
        Assert.Contains("cannot be maintained", fault.Message, StringComparison.Ordinal);
        Assert.True(fault.Message.Contains(says, StringComparison.OrdinalIgnoreCase), fault.Message);
        Assert.Contains("ViewMaintenance.Recompute", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>The reference executor recomputes: a maintained view over it is refused.</summary>
    [Fact]
    public async Task A_maintained_view_on_the_reference_executor_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(ExecutionEngine.Reference);
        var fault = await Assert.ThrowsAsync<StandingViewException>(async () =>
            await desk.Engine.MaterializeAsync(Totals, Options(ViewMaintenance.Incremental), Ct));
        Assert.Equal(ChalkErrorCodes.UnsupportedInView, fault.Code);
        Assert.Contains("reference executor", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An update that fails part way leaves the view at the epoch it held; the next starts from
    /// nothing, and a reader gets only the keys that differ from what it holds.
    /// </summary>
    [Fact]
    public async Task After_a_failed_update_the_next_starts_again()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var armed = false;
        await using var desk = await DeskAsync(trip: seq => !(armed && seq == 5)
            ? true
            : throw new InvalidOperationException("tripped"));
        const string Tripped = "SELECT book, SUM(qty) AS qty FROM fills WHERE trip(seq) GROUP BY book";

        // Six books, so that the three the next updates touch are fewer than the rows: no reset for size.
        await desk.Engine.RefreshAsync(
            r => r.Append(desk.Source, "fills", Fills(
                new Fill(10, 4, 40, Open.AddSeconds(2)), new Fill(11, 5, 50, Open.AddSeconds(2)), new Fill(12, 6, 60, Open.AddSeconds(2)))),
            Ct);
        await using var view = await desk.Engine.MaterializeAsync(Tripped, Options(ViewMaintenance.Incremental), Ct);
        var replica = new Replica();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, replica);
        var held = view.Version;

        armed = true;
        await desk.Engine.RefreshAsync(
            r => r.Version(held + 1).Append(desk.Source, "fills", Fills(new Fill(4, 1, 1, Open.AddSeconds(3)), new Fill(5, 2, 2, Open.AddSeconds(4)))),
            Ct);
        await Assert.ThrowsAnyAsync<Exception>(async () => await view.UpdateAsync(Ct));
        Assert.Equal(held, view.Version);

        armed = false;
        await desk.Engine.RefreshAsync(r => r.Version(held + 2).Append(desk.Source, "fills", Fills(new Fill(6, 3, 3, Open.AddSeconds(5)))), Ct);
        Assert.Equal(held + 2, await view.UpdateAsync(Ct));
        replica.Calls.Clear();
        view.Drain(ref cursor, replica);
        Assert.Equal([$"commit {held + 2}", "upsert 1=11", "upsert 2=22", "upsert 3=33"], replica.Calls.Order(StringComparer.Ordinal));
        await AssertIsTheStatementAsync(desk.Engine, replica, Tripped);
    }

    /// <summary>A Map that gives a new key the host key an unchanged row holds faults a maintained view too.</summary>
    [Fact]
    public async Task A_map_that_merges_a_new_key_into_a_held_one_faults_the_view()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, new ViewOptions<BookKey, BookRow>
        {
            Key = ["book"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Maintenance = ViewMaintenance.Incremental,
            Map = static (in ViewRow row, out BookKey key, out BookRow value) =>
            {
                // Books 1, 2 and 3 keep three host keys; book 4 lands on book 1's.
                key = new BookKey { Book = row.GetInt64(0) == 4 ? 1 : row.GetInt64(0) };
                value = new BookRow { Qty = row.GetInt64(1) };
            },
        }, Ct);

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(new Fill(4, 4, 7, Open.AddSeconds(3)))), Ct);
        var fault = await Assert.ThrowsAsync<StandingViewException>(async () => await view.UpdateAsync(Ct));
        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, fault.Code);
        Assert.Contains("mapped to one host key", fault.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<StandingViewException>(async () => await view.UpdateAsync(Ct));
    }

    /// <summary>
    /// A view whose as-of join has a constant key on its right side, which the planner reads for many
    /// left rows through an index lookup of that key, is maintained (design 68 §19): after appends, a
    /// second rate at one time and a compaction of the rates, it is the statement. The constant may be
    /// written in the join, or as a one-row table crossed in, which the planner makes a column of
    /// literals (D348) rather than a semi join against one row.
    /// </summary>
    [Theory]
    [InlineData("FROM fills f ASOF JOIN rates r MATCH_CONDITION r.ts <= f.ts ON r.symbol = 'EUR'", true)]
    [InlineData(
        "FROM fills f CROSS JOIN (SELECT CAST('EUR' AS VARCHAR(32)) AS symbol) x "
        + "ASOF JOIN rates r MATCH_CONDITION r.ts <= f.ts ON r.symbol = x.symbol",
        false)]
    public async Task A_view_over_an_as_of_join_with_a_constant_key_is_maintained(string from, bool overALookup)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var converted = $"SELECT f.book, SUM(CAST(f.qty * r.usd * 1000 AS BIGINT)) AS qty {from} GROUP BY f.book";
        string[] symbols = ["EUR", "GBP", "USD"];
        var fills = Enumerable.Range(1, 1_500).Select(i => new Fill(i, 1 + (i % 3), i % 7, Open.AddSeconds(i))).ToList();
        var rates = Enumerable.Range(0, 600)
            .SelectMany(i => symbols.Select(symbol => new Rate(symbol, Open.AddSeconds(i * 3), 1 + (i % 10 / 10.0))))
            .ToList();
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)fills, t => t.AppendOnly(x => x.Ts))
            .AddTable("rates", (IReadOnlyList<Rate>)rates, t =>
            {
                t.OrderedBy(x => x.Ts).ThenBy(x => x.Symbol);
                t.Index(x => x.Symbol, x => x.Ts);
            })
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open, TimeSpan.Zero)), Ct);
        var prepared = await engine.PrepareAsync(converted, ct: Ct);
        Assert.True(
            ReadsRatesThroughALookup(prepared.Plan.Root) == overALookup,
            $"the as-of join is {(overALookup ? "not " : string.Empty)}planned over a lookup: {prepared.Plan.Root}");

        await using var view = await engine.MaterializeAsync(converted, Options(ViewMaintenance.Incremental), Ct);
        var replica = new Replica();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, replica);
        await AssertIsTheStatementAsync(engine, replica, converted);

        // Fills after every rate, and new rates with a second EUR rate at their time, which ties.
        var at = Open.AddSeconds(1_800);
        List<Fill> later = [.. Enumerable.Range(1_501, 9).Select(i => new Fill(i, 1 + (i % 3), 3, at.AddSeconds(i - 1_500)))];
        List<Rate> newer = [new("EUR", at, 2.5), new("EUR", at, 3.5), new("GBP", at, 1.5), new("USD", at, 1)];
        rates.AddRange(newer);
        await engine.RefreshAsync(r =>
        {
            r.Append(source, "fills", (IReadOnlyList<Fill>)later);
            r.Append(source, "rates", (IReadOnlyList<Rate>)newer);
        }, Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        await AssertIsTheStatementAsync(engine, replica, converted);

        // A compaction: every other rate of the older half gone, the rest the same objects in their order.
        var half = rates.Count / 2;
        rates = [.. rates.Where((_, i) => i >= half || i % 2 == 0)];
        var kept = rates;
        await engine.RefreshAsync(r => r.Replace(source, "rates", (IReadOnlyList<Rate>)kept), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        await AssertIsTheStatementAsync(engine, replica, converted);
    }

    /// <summary>Whether the plan has an as-of join whose right input is an index lookup, through filters and projections.</summary>
    private static bool ReadsRatesThroughALookup(Google.Protobuf.IMessage message)
    {
        if (message is Chalk.Ir.Rel { KindCase: Chalk.Ir.Rel.KindOneofCase.AsOfJoin } join)
        {
            var right = join.AsOfJoin.Right;
            while (right.KindCase is Chalk.Ir.Rel.KindOneofCase.Filter or Chalk.Ir.Rel.KindOneofCase.Project)
            {
                right = right.KindCase == Chalk.Ir.Rel.KindOneofCase.Filter ? right.Filter.Input : right.Project.Input;
            }

            if (right.KindCase == Chalk.Ir.Rel.KindOneofCase.IndexLookup)
            {
                return true;
            }
        }

        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            switch (field.Accessor.GetValue(message))
            {
                case Google.Protobuf.IMessage child when ReadsRatesThroughALookup(child):
                    return true;
                case System.Collections.IList list:
                    foreach (var item in list)
                    {
                        if (item is Google.Protobuf.IMessage element && ReadsRatesThroughALookup(element))
                        {
                            return true;
                        }
                    }

                    break;
            }
        }

        return false;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<Fill> Fills(params Fill[] fills) => fills;

    private static ViewOptions<BookKey, BookRow> Options(ViewMaintenance maintenance, long budget = 64L << 20) => new()
    {
        Key = ["book"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        StateBudget = budget,
        Map = static (in ViewRow row, out BookKey key, out BookRow value) =>
        {
            key = new BookKey { Book = row.GetInt64(0) };
            value = new BookRow { Qty = row.GetInt64(1) };
        },
    };

    /// <summary>The replica equals what the statement returns now.</summary>
    private static async Task AssertIsTheStatementAsync(ChalkEngine engine, Replica replica, string sql = Totals)
    {
        var expected = new Dictionary<long, long>();
        await foreach (var batch in engine.QueryAsync(sql, ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    expected[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                        Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
                }
            }
        }

        Assert.Equal(expected, replica.Rows);
    }

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required PocoSource Source { get; init; }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private async Task<Desk> DeskAsync(ExecutionEngine executor = ExecutionEngine.Vectorised, Func<int, bool>? trip = null)
    {
        var builder = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)FirstFills, t => t.AppendOnly(x => x.Ts));
        if (trip is not null)
        {
            builder.AddFunction("trip", f => f.Scalar<int, bool>("seq").Strict().Client());
        }

        var source = builder.Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
            Execution = new ExecutionOptions { Engine = executor },
            Functions = trip is null ? null : registry => registry.AddScalar<int, bool>("trip", seq => trip(seq)),
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open, TimeSpan.Zero)), Ct);
        return new Desk { Engine = engine, Source = source };
    }

    /// <summary>A sink that records each call and keeps a replica of the rows, as a host's would.</summary>
    private sealed class Replica : IViewSink<BookKey, BookRow>
    {
        public List<string> Calls { get; } = [];

        public Dictionary<long, long> Rows { get; } = [];

        public void Reset()
        {
            Calls.Add("reset");
            Rows.Clear();
        }

        public void Upsert(in BookKey key, in BookRow row)
        {
            Calls.Add($"upsert {key.Book}={row.Qty}");
            Rows[key.Book] = row.Qty;
        }

        public void Delete(in BookKey key)
        {
            Calls.Add($"delete {key.Book}");
            Rows.Remove(key.Book);
        }

        public void Commit(long version, uint checksum) => Calls.Add($"commit {version}");
    }
}
