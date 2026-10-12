using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A standing view (D339, <c>docs/design/64-incremental-views.md</c> §4–§6) as a host uses one: made
/// over a statement, updated after each refresh, and drained from cursors into a sink that keeps a
/// replica. After every drain the replica must be the view, and the view must be the statement.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StandingViewTests(SharedSidecar sidecar)
{
    private sealed record Fill(int Seq, int Book, long Qty, DateTime Ts);

    private struct BookKey
    {
        public long Book;
    }

    private struct BookRow
    {
        public long Qty;
    }

    private const string Totals = "SELECT book, SUM(qty) AS qty FROM fills GROUP BY book";

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Fill[] FirstFills =
    [
        new(1, 1, 10, Open),
        new(2, 2, 20, Open.AddSeconds(1)),
        new(3, 3, 30, Open.AddSeconds(2)),
    ];

    /// <summary>
    /// A reader's first drain is a reset: every row, then a commit with the view's version and the
    /// sum of the rows' hashes.
    /// </summary>
    [Fact]
    public async Task A_first_drain_resets_and_sends_every_row()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());

        var sink = new Replica();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));

        Assert.Equal(["reset", "upsert 1=10", "upsert 2=20", "upsert 3=30", "commit 0"], sink.Calls);
        Assert.Equal(sink.Checksum(), sink.CommittedChecksum);
        Assert.Equal(view.Checksum, sink.CommittedChecksum);
        Assert.Equal(3, view.Count);
        Assert.False(cursor.IsStart);
    }

    /// <summary>
    /// After a refresh, a drain sends only the keys whose rows changed, never a row equal to the one
    /// the key held, and a key the result no longer has as a delete.
    /// </summary>
    [Fact]
    public async Task A_drain_sends_only_what_changed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await desk.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(new Fill(4, 4, 1, Open.AddSeconds(3)), new Fill(5, 5, 2, Open.AddSeconds(3)))));
        await using var view = await desk.Engine.MaterializeAsync(
            "SELECT book, SUM(qty) AS qty FROM fills GROUP BY book HAVING SUM(qty) < 40", Options());
        var (sink, cursor) = Drained(view);

        await desk.RefreshAsync(r => r.Version(3).Append(desk.Source, "fills", Fills(new Fill(6, 1, 5, Open.AddSeconds(4)), new Fill(7, 3, 15, Open.AddSeconds(4)))));
        Assert.Equal(3, await view.UpdateAsync());
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));

        Assert.Equal(["upsert 1=15", "delete 3", "commit 3"], sink.Calls);
        Assert.Equal(new Dictionary<long, long> { [1] = 15, [2] = 20, [4] = 1, [5] = 2 }, sink.Rows);
        Assert.Equal(sink.Checksum(), sink.CommittedChecksum);
        await AssertViewIsTheStatementAsync(desk.Engine, view, "SELECT book, SUM(qty) AS qty FROM fills GROUP BY book HAVING SUM(qty) < 40", sink);
    }

    /// <summary>
    /// An update at the epoch the view already holds does nothing, and a drain level with it calls
    /// nothing either.
    /// </summary>
    [Fact]
    public async Task An_update_with_nothing_new_does_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());
        var (sink, cursor) = Drained(view);

        Assert.Equal(0, await view.UpdateAsync());
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        Assert.Empty(sink.Calls);
    }

    /// <summary>
    /// A refresh that changes no row the view reads moves its version all the same, so a drain
    /// commits the new version with no rows.
    /// </summary>
    [Fact]
    public async Task A_new_version_with_no_change_commits_without_rows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());
        var (sink, cursor) = Drained(view);

        await desk.RefreshAsync(r => r.Version(9));
        await view.UpdateAsync();
        view.Drain(ref cursor, sink);

        Assert.Equal(["commit 9"], sink.Calls);
    }

    /// <summary>
    /// Out of budget a drain stops without committing, and the next one carries on; a key that
    /// changed again in between is sent again with its latest row, and the commit comes once the
    /// reader is level with the view.
    /// </summary>
    [Fact]
    public async Task A_partial_drain_resumes_across_an_epoch()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());
        var (sink, cursor) = Drained(view);

        await desk.RefreshAsync(r => r.Version(2).Append(desk.Source, "fills", Fills(new Fill(4, 1, 1, Open.AddSeconds(3)), new Fill(5, 2, 1, Open.AddSeconds(4)))));
        await view.UpdateAsync();
        Assert.Equal(DrainOutcome.Partial, view.Drain(ref cursor, sink, budget: 1));
        Assert.Equal(["upsert 1=11"], sink.Calls);

        await desk.RefreshAsync(r => r.Version(3).Append(desk.Source, "fills", Fills(new Fill(6, 1, 1, Open.AddSeconds(5)))));
        await view.UpdateAsync();
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink, budget: 10));

        Assert.Equal(["upsert 1=11", "upsert 2=21", "upsert 1=12", "commit 3"], sink.Calls);
        Assert.Equal(3, cursor.Version);
        Assert.Equal(sink.Checksum(), sink.CommittedChecksum);
    }

    /// <summary>Each reader holds its own cursor over the one view, and is sent what it has not seen.</summary>
    [Fact]
    public async Task Each_reader_is_sent_what_it_has_not_seen()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());
        var (early, earlyCursor) = Drained(view);

        await desk.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(new Fill(4, 1, 5, Open.AddSeconds(3)))));
        await view.UpdateAsync();
        var (late, lateCursor) = Drained(view);
        view.Drain(ref earlyCursor, early);

        await desk.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(new Fill(5, 2, 5, Open.AddSeconds(4)))));
        await view.UpdateAsync();
        view.Drain(ref earlyCursor, early);
        view.Drain(ref lateCursor, late);

        Assert.Equal(["upsert 1=15", "commit 1", "upsert 2=25", "commit 2"], early.Calls);
        Assert.Equal(["upsert 2=25", "commit 2"], late.Calls);
        Assert.Equal(early.Rows, late.Rows);
    }

    /// <summary>
    /// A reader so far behind that what changed is no fewer rows than the view holds gets the whole
    /// result again, as a reset: no more to send than the changes.
    /// </summary>
    [Fact]
    public async Task A_reader_far_behind_is_reset()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());
        var (sink, cursor) = Drained(view);

        await desk.RefreshAsync(r => r.Append(desk.Source, "fills", Fills(
            new(4, 1, 1, Open.AddSeconds(3)), new(5, 2, 1, Open.AddSeconds(3)), new(6, 3, 1, Open.AddSeconds(3)))));
        await view.UpdateAsync();
        view.Drain(ref cursor, sink);

        Assert.Equal(["reset", "upsert 1=11", "upsert 2=21", "upsert 3=31", "commit 1"], sink.Calls);
    }

    /// <summary>
    /// Deletes are kept until they outnumber the live rows; a reader whose next drain needs one that
    /// has gone is reset rather than left with a key it should have dropped.
    /// </summary>
    [Fact]
    public async Task A_reader_that_needs_a_dropped_delete_is_reset()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        const string Recent = "SELECT book, SUM(qty) AS qty FROM fills WHERE seq >= @from GROUP BY book";
        await using var view = await desk.Engine.MaterializeAsync(Recent, Options(new Dictionary<string, object?> { ["from"] = 1 }));
        var (sink, cursor) = Drained(view);

        // A rewrite to rows of one book: two deletes and one live row, so the oldest delete goes.
        await desk.RefreshAsync(r => r.Replace(desk.Source, "fills", Fills(new Fill(7, 1, 1, Open.AddSeconds(9)))));
        await view.UpdateAsync();
        view.Drain(ref cursor, sink);

        Assert.Equal("reset", sink.Calls[0]);
        Assert.Equal(new Dictionary<long, long> { [1] = 1 }, sink.Rows);
        Assert.Equal(1, view.Count);
    }

    /// <summary>
    /// A key the result gives twice faults the view, naming the key, the version and both rows; the
    /// view answers every call with that fault until it is disposed.
    /// </summary>
    [Fact]
    public async Task A_key_given_twice_faults_the_view()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(
            "SELECT book, qty FROM fills", Options());
        var (sink, cursor) = Drained(view);

        await desk.RefreshAsync(r => r.Version(4).Append(desk.Source, "fills", Fills(new Fill(4, 2, 7, Open.AddSeconds(3)))));
        var fault = await Assert.ThrowsAsync<StandingViewException>(async () => await view.UpdateAsync());

        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, fault.Code);
        Assert.Contains("The view's key (book) = (2) is given by two rows of the result at version 4", fault.Message, StringComparison.Ordinal);
        Assert.Same(fault, Assert.Throws<StandingViewException>(() => view.Drain(ref cursor, sink)));
        Assert.Same(fault, await Assert.ThrowsAsync<StandingViewException>(async () => await view.UpdateAsync()));
    }

    /// <summary>A Map that gives two keys one host key breaks the view's key just as a duplicate does.</summary>
    [Fact]
    public async Task A_map_that_merges_two_keys_faults_the_view()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();

        var fault = await Assert.ThrowsAsync<StandingViewException>(async () =>
            await desk.Engine.MaterializeAsync(Totals, new ViewOptions<BookKey, BookRow>
            {
                Key = ["book"],
                Map = static (in ViewRow row, out BookKey key, out BookRow value) =>
                {
                    key = new BookKey { Book = row.GetInt64(0) % 2 };
                    value = new BookRow { Qty = row.GetInt64(1) };
                },
            }));

        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, fault.Code);
        Assert.Contains("correct the statement's key, or the Map", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// In a view, the parameter named as the engine's clock reads the clock as of the view's epoch,
    /// and takes no value from the host.
    /// </summary>
    [Fact]
    public async Task The_clock_parameter_reads_the_epochs_clock()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await desk.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddSeconds(1))));
        const string Until = "SELECT book, SUM(qty) AS qty FROM fills WHERE ts <= CAST(@now AS TIMESTAMP(9)) GROUP BY book";
        await using var view = await desk.Engine.MaterializeAsync(Until, Options());
        var (sink, cursor) = Drained(view);
        Assert.Equal(new Dictionary<long, long> { [1] = 10, [2] = 20 }, sink.Rows);

        await desk.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddSeconds(2))));
        await view.UpdateAsync();
        view.Drain(ref cursor, sink);
        Assert.Equal(new Dictionary<long, long> { [1] = 10, [2] = 20, [3] = 30 }, sink.Rows);

        var refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await desk.Engine.MaterializeAsync(Until, Options(new Dictionary<string, object?> { ["now"] = Open })));
        Assert.Contains("in a view @now is the engine's clock", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The current time functions read the clock in a view too.</summary>
    [Fact]
    public async Task The_current_time_reads_the_epochs_clock()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await desk.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddSeconds(1))));
        const string Until = "SELECT book, SUM(qty) AS qty FROM fills WHERE ts <= CURRENT_TIMESTAMP GROUP BY book";
        await using var view = await desk.Engine.MaterializeAsync(Until, Options());
        var (sink, cursor) = Drained(view);
        Assert.Equal(new Dictionary<long, long> { [1] = 10, [2] = 20 }, sink.Rows);

        await desk.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddSeconds(2))));
        await view.UpdateAsync();
        view.Drain(ref cursor, sink);
        Assert.Equal(new Dictionary<long, long> { [1] = 10, [2] = 20, [3] = 30 }, sink.Rows);
    }

    /// <summary>A view that reads the clock before any refresh has set it is refused, saying how to set it.</summary>
    [Fact]
    public async Task A_view_that_reads_an_unset_clock_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();

        var refused = await Assert.ThrowsAsync<StandingViewException>(async () =>
            await desk.Engine.MaterializeAsync(
                "SELECT book, SUM(qty) AS qty FROM fills WHERE ts <= CAST(@now AS TIMESTAMP(9)) GROUP BY book", Options()));

        Assert.Equal(ChalkErrorCodes.ClockRequired, refused.Code);
        Assert.Contains("Advance it in a refresh with AdvanceClock", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="StandingView{TKey, TRow}.ReachedAsync"/> waits for the engine to publish a version
    /// and brings the view to it: the rendezvous for "after version k".
    /// </summary>
    [Fact]
    public async Task Reached_waits_for_a_version_and_updates_to_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        await using var view = await desk.Engine.MaterializeAsync(Totals, Options());

        var reached = view.ReachedAsync(12, TestContext.Current.CancellationToken).AsTask();
        Assert.False(reached.IsCompleted);

        await desk.RefreshAsync(r => r.Version(12).Append(desk.Source, "fills", Fills(new Fill(4, 1, 1, Open.AddSeconds(3)))));
        await reached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(12, view.Version);
    }

    /// <summary>
    /// A refresh that lands while an update reads is never half seen: the update notices the engine
    /// moved, and runs again at the new epoch.
    /// </summary>
    [Fact]
    public async Task An_update_a_refresh_overtakes_runs_again()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync();
        var overtake = 0;
        var mapped = 0;
        var options = new ViewOptions<BookKey, BookRow>
        {
            Key = ["book"],
            Map = (in ViewRow row, out BookKey key, out BookRow value) =>
            {
                mapped++;
                if (Interlocked.Exchange(ref overtake, 0) == 1)
                {
                    desk.RefreshAsync(r => r.Version(5).Append(desk.Source, "fills", Fills(new Fill(4, 3, 3, Open.AddSeconds(3)))))
                        .GetAwaiter().GetResult();
                }

                key = new BookKey { Book = row.GetInt64(0) };
                value = new BookRow { Qty = row.GetInt64(1) };
            },
        };
        await using var view = await desk.Engine.MaterializeAsync(Totals, options);
        var before = mapped;

        await desk.RefreshAsync(r => r.Version(4).Append(desk.Source, "fills", Fills(new Fill(5, 1, 1, Open.AddSeconds(3)))));
        overtake = 1;
        Assert.Equal(5, await view.UpdateAsync());

        var (sink, _) = Drained(view);
        Assert.Equal(new Dictionary<long, long> { [1] = 11, [2] = 20, [3] = 33 }, sink.Rows);
        Assert.True(mapped - before > 3, "the update ran again after the refresh landed");
    }

    // ---- the scaffolding ----

    private static ViewOptions<BookKey, BookRow> Options(object? parameters = null) => new()
    {
        Key = ["book"],
        Map = static (in ViewRow row, out BookKey key, out BookRow value) =>
        {
            key = new BookKey { Book = row.GetInt64(0) };
            value = new BookRow { Qty = row.GetInt64(1) };
        },
        RowHash = Fnv,
        Parameters = parameters,
    };

    private static uint Fnv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
    {
        var hash = 2166136261u;
        foreach (var b in key)
        {
            hash = (hash ^ b) * 16777619u;
        }

        foreach (var b in row)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    private static IReadOnlyList<Fill> Fills(params Fill[] fills) => fills;

    private static (Replica Sink, ViewCursor Cursor) Drained(StandingView<BookKey, BookRow> view)
    {
        var sink = new Replica();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        sink.Calls.Clear();
        return (sink, cursor);
    }

    /// <summary>The replica equals what the statement returns now.</summary>
    private static async Task AssertViewIsTheStatementAsync(
        ChalkEngine engine, StandingView<BookKey, BookRow> view, string sql, Replica replica)
    {
        var expected = new Dictionary<long, long>();
        await foreach (var batch in engine.QueryAsync(sql, ct: TestContext.Current.CancellationToken))
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
        Assert.Equal(expected.Count, view.Count);
    }

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required PocoSource Source { get; init; }

        public async Task RefreshAsync(Action<RefreshBuilder> plan) =>
            await Engine.RefreshAsync(plan, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private async Task<Desk> DeskAsync()
    {
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)FirstFills, t => t.AppendOnly(x => x.Ts))
            .Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, TestContext.Current.CancellationToken);
        return new Desk { Engine = engine, Source = source };
    }

    /// <summary>A sink that records each call and keeps a replica of the rows, as a host's would.</summary>
    private sealed class Replica : IViewSink<BookKey, BookRow>
    {
        public List<string> Calls { get; } = [];

        public Dictionary<long, long> Rows { get; } = [];

        public uint CommittedChecksum { get; private set; }

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

        public void Commit(long version, uint checksum)
        {
            Calls.Add($"commit {version}");
            CommittedChecksum = checksum;
        }

        /// <summary>The sum of the replica's rows' hashes, as the view keeps it.</summary>
        public uint Checksum()
        {
            var sum = 0u;
            foreach (var (book, qty) in Rows)
            {
                var key = new BookKey { Book = book };
                var row = new BookRow { Qty = qty };
                sum += Fnv(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BookKey>(in key)),
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BookRow>(in row)));
            }

            return sum;
        }
    }
}
