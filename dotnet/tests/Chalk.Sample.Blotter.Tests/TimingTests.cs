using Chalk.Sample.Blotter.Alerts;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Fixtures;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// The timing rules of <c>TimingPartitions.md</c>, each met at its boundary: a host and a house on a
/// <see cref="FakeTimeProvider" />, turned by hand, so that every check runs at an exact instant, just
/// before a boundary, on it, and just after.
/// </summary>
/// <remarks>
/// The tables and the engine's clock hold microseconds, so a step past a boundary is a microsecond.
/// Pages are checked against <see cref="Oracle" />: shown means what the oracle test means by it.
/// </remarks>
public sealed class TimingTests
{
    private static readonly TimeSpan Micro = TimeSpan.FromMicroseconds(1);

    // ---- 1. The gap fill on going live ----

    [Fact]
    public async Task Gap_fill_brings_what_was_written_before_it_and_the_stream_what_was_sent_after()
    {
        // The database holds a version 1.5 s after it is sent, and orders_current 30 s after that.
        await using var house = await SteppedAsync(sync: CurrentSync.Lagging, written: 1.5, current: 30);
        var t0 = house.Now;

        var before = house.Send(20);
        await house.AtAsync(t0 + TimeSpan.FromSeconds(2));
        await house.GoLiveAsync();

        await house.AtAsync(t0 + TimeSpan.FromSeconds(3));
        var after = house.Send(20);
        await house.TurnAsync();

        Assert.Null(await house.ShownAsync(before));
        Assert.Null(await house.ShownAsync(after));
    }

    [Fact]
    public async Task Gap_fill_waits_out_the_haircut_and_brings_a_version_in_transit_when_the_host_subscribed()
    {
        // Sent at t0 and written at t0 + 1.5 s; the host subscribes at t0 + 1 s, and its one gap fill
        // reads 2 s later, once the version is in the database.
        await using var house = await SteppedAsync(sync: CurrentSync.Lagging, written: 1.5, current: 30, haircut: TimeSpan.FromSeconds(2));
        var t0 = house.Now;

        var inTransit = house.Send(20);
        await house.AtAsync(t0 + TimeSpan.FromSeconds(1));
        await house.GoLiveAsync();
        await house.TurnAsync();
        Assert.Null(await house.ShownAsync(inTransit));
    }

    [Fact]
    public async Task Gap_fill_misses_a_version_still_in_transit_after_the_haircut()
    {
        // Written 3.5 s after it is sent, longer than the haircut covers: the host's one assumption broken,
        // and it shows the version orders_current had until orders_current holds it.
        await using var house = await SteppedAsync(sync: CurrentSync.Lagging, written: 3.5, current: 30, haircut: TimeSpan.FromSeconds(2));
        var t0 = house.Now;

        var late = house.Send(20);
        await house.AtAsync(t0 + TimeSpan.FromSeconds(1));
        await house.GoLiveAsync();
        await house.AtAsync(t0 + TimeSpan.FromSeconds(4));
        Assert.NotNull(await house.ShownAsync(late));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Gap_fill_reaches_back_GapFill_from_going_live_and_not_a_microsecond_more(int pastMicros, bool brought)
    {
        // Every change works an order already open, so each sends exactly one version.
        await using var house = await SteppedAsync(
            sync: CurrentSync.Lagging, written: 1, current: 60, gapFill: TimeSpan.FromSeconds(5), newShare: 0);
        var sent = house.Send(1);
        var version = Assert.Single(sent);

        await house.AtAsync(house.Now + TimeSpan.FromSeconds(2));
        await house.AtAsync(version.EventTime + TimeSpan.FromSeconds(5) + pastMicros * Micro);
        await house.GoLiveAsync();
        await house.TurnAsync();

        // Brought: shown at once. Not brought: orders_current still has the version before, for a
        // minute; the host assumes it holds anything older than the gap fill reaches.
        var shown = await house.ShownAsync(sent);
        Assert.True(brought == (shown is null), shown ?? "shown, though older than the gap fill reaches");
    }

    // ---- 2 and 3. Confirmation and eviction ----

    [Fact]
    public async Task Confirmation_looks_the_tail_up_once_ConfirmEvery_has_passed_and_not_before()
    {
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 60);
        await house.GoLiveAsync();
        house.Send(5);
        await house.TurnAsync();
        var first = house.Host.Status.Confirmations;

        await house.AtAsync(house.Now + TimeSpan.FromMilliseconds(500) - Micro);
        Assert.Equal(first, house.Host.Status.Confirmations);

        await house.AtAsync(house.Now + Micro);
        Assert.Equal(first + 1, house.Host.Status.Confirmations);
    }

    [Fact]
    public async Task Eviction_takes_a_confirmed_order_EvictAfter_after_confirmation_and_not_a_microsecond_before()
    {
        // A lookup on every turn, so that only EvictAfter decides.
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1, confirmEvery: Micro);
        await house.GoLiveAsync();
        var t0 = house.Now;
        var sent = house.Send(5);
        t0 = house.Now;
        await house.TurnAsync();
        var ids = sent.Select(v => v.OrderId).Distinct().ToList();
        Assert.All(ids, id => Assert.True(house.Host.Reconciling.ContainsKey(id)));

        // Written, and confirmed on the same turn.
        await house.AtAsync(t0 + TimeSpan.FromSeconds(1));
        var confirmed = house.Now;

        await house.AtAsync(confirmed + TimeSpan.FromSeconds(5) - Micro);
        Assert.All(ids, id => Assert.True(house.Host.Reconciling.ContainsKey(id), "evicted before EvictAfter"));

        await house.AtAsync(confirmed + TimeSpan.FromSeconds(5));
        Assert.All(ids, id => Assert.False(house.Host.Reconciling.ContainsKey(id), "kept on EvictAfter"));
        Assert.Null(await house.ShownAsync(sent));
    }

    [Fact]
    public void Eviction_restarts_when_a_newer_version_arrives_after_confirmation()
    {
        var tail = new Tail();
        var at = new DateTime(2026, 10, 25, 12, 0, 0);
        var v1 = Order(1, at);
        tail.Set(new TailEntry(v1, null), v1);
        var entry = tail.Get(v1.OrderId)!;
        tail.Confirm(entry, at.AddSeconds(1));
        Assert.Equal(at.AddSeconds(1), entry.ConfirmedAt);

        var v2 = v1 with { Version = 2, EventTime = at.AddSeconds(2) };
        entry.Live = v2;
        tail.Set(entry, v2);
        Assert.Null(entry.ConfirmedAt);
    }

    // ---- 4. Kept windows ----

    [Fact]
    public async Task Kept_windows_answer_pages_until_WindowFor_and_are_read_again_a_microsecond_after()
    {
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1);
        await house.GoLiveAsync();
        var scope = await house.AcquireAsync(Tabs.Orders, FilterSpec.None);
        var view = house.Host.Pages.View(Tabs.Orders.DefaultSort);
        await view.GetAsync(scope.State, 100, 20, CancellationToken.None);
        var read = house.Now;

        // WindowFor is EvictAfter less a second: 4 s here.
        await house.AtAsync(read + TimeSpan.FromSeconds(4));
        var hits = house.Host.Pages.WindowHits;
        await house.Host.Pages.View(Tabs.Orders.DefaultSort).GetAsync(scope.State, 100, 20, CancellationToken.None);
        Assert.Equal(hits + 1, house.Host.Pages.WindowHits);

        await house.AtAsync(read + TimeSpan.FromSeconds(4) + Micro);
        hits = house.Host.Pages.WindowHits;
        await house.Host.Pages.View(Tabs.Orders.DefaultSort).GetAsync(scope.State, 100, 20, CancellationToken.None);
        Assert.Equal(hits, house.Host.Pages.WindowHits);
        await house.ReleaseAsync(scope);
    }

    // ---- 6. Scope grace ----

    [Fact]
    public async Task Scope_grace_keeps_a_scope_no_page_uses_for_ScopeGrace_and_closes_it_a_microsecond_after()
    {
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1);
        await house.GoLiveAsync();
        var filter = FilterSpec.Of([new ColumnFilter { Column = Tabs.Orders.Named("client"), Values = ["CEDAR"] }]);
        var scope = await house.AcquireAsync(Tabs.Orders, filter);
        await house.ReleaseAsync(scope);
        var idle = house.Now;

        await house.AtAsync(idle + TimeSpan.FromSeconds(20));
        Assert.Contains(house.Host.Scopes, s => s.Name == scope.Name);

        await house.AtAsync(idle + TimeSpan.FromSeconds(20) + Micro);
        Assert.DoesNotContain(house.Host.Scopes, s => s.Name == scope.Name);
    }

    // ---- 9 and 10. Lag and count alerts ----

    [Fact]
    public async Task Lag_alerts_fire_a_microsecond_past_the_wait_and_not_on_it()
    {
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1, clockEvery: Micro);
        await house.GoLiveAsync();
        await house.ClockSetAsync();
        var (session, fired) = await house.AlertsAsync(Rule("late", Tabs.Orders, "", lagOver: TimeSpan.FromSeconds(1)));

        house.Host.Exchange.StallIngestion(TimeSpan.FromMinutes(5));
        house.Send(5);
        await house.TurnAsync();

        // The oldest wait the host holds, the version that set the clock among them.
        var since = Cells.Micros(house.Host.Reconciling.Values.Min(r => r.Since));

        await house.AtAsync(since + TimeSpan.FromSeconds(1));
        Assert.Empty(fired);

        await house.AtAsync(since + TimeSpan.FromSeconds(1) + Micro);
        Assert.NotEmpty(fired);
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task Count_alerts_judge_none_from_one_window_after_counting_began_and_not_before()
    {
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1, clockEvery: Micro);
        await house.GoLiveAsync();
        await house.ClockSetAsync();
        var (session, fired) = await house.AlertsAsync(
            Rule("quiet", Tabs.Orders, "client = 'NOBODY'", count: new AlertCount(CountKind.None, 0, TimeSpan.FromSeconds(5))));
        var began = house.Now;

        await house.AtAsync(began + TimeSpan.FromSeconds(5) - Micro);
        Assert.Empty(fired);

        await house.AtAsync(began + TimeSpan.FromSeconds(5));
        Assert.Equal("none in the last 5 s", Assert.Single(fired).Latest);
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task Count_alerts_drop_a_slice_exactly_one_window_after_it_starts()
    {
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1, clockEvery: Micro);
        await house.GoLiveAsync();
        await house.ClockSetAsync();
        var (session, fired) = await house.AlertsAsync(
            Rule("quiet", Tabs.Fills, "qty > 0", count: new AlertCount(CountKind.None, 0, TimeSpan.FromSeconds(5))));
        var began = house.Now;

        // Fills at the start of a whole second, counted in that second's slice, which the next turn writes.
        var slice = began.AddTicks(-(began.Ticks % TimeSpan.TicksPerSecond)) + TimeSpan.FromSeconds(1);
        await house.AtAsync(slice);
        Assert.Contains(house.Send(50), _ => true);
        await house.TurnAsync();
        await house.AtAsync(house.Now + Micro);

        // The slice counts while it starts within the window: until exactly 5 s after it starts.
        await house.AtAsync(slice + TimeSpan.FromSeconds(5) - Micro);
        Assert.Empty(fired);

        await house.AtAsync(slice + TimeSpan.FromSeconds(5));
        Assert.Single(fired);
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task Count_alerts_added_before_the_host_first_refreshes_run()
    {
        // A quiet market: going live takes nothing, so nothing has refreshed the engine yet.
        await using var house = await SteppedAsync(sync: CurrentSync.Trigger, written: 1, clockEvery: Micro);
        await house.GoLiveAsync();
        var (session, fired) = await house.AlertsAsync(
            Rule("quiet", Tabs.Orders, "client = 'NOBODY'", count: new AlertCount(CountKind.None, 0, TimeSpan.FromSeconds(5))));

        await house.AtAsync(house.Now + TimeSpan.FromSeconds(6));
        Assert.Single(fired);
        GC.KeepAlive(session);
    }

    // ---- 11. Alert sessions ----

    [Fact]
    public void Alert_sessions_sound_on_the_throttle_and_gather_within_it()
    {
        var at = new DateTime(2026, 10, 25, 12, 0, 0);
        var rule = Rule("r", Tabs.Fills, "") with { Throttle = TimeSpan.FromSeconds(3) };
        var session = new AlertSession("browser");
        var fired = new List<AlertEvent>();
        session.Fired += fired.Add;
        session.SetRules([rule]);
        var view = AlertViewSpec.Of(rule).Name;

        session.Enter(view, [(Fill(at), null)], at);
        session.Enter(view, [(Fill(at), null)], at + TimeSpan.FromSeconds(1));
        session.Flush(at + TimeSpan.FromSeconds(3) - Micro);
        Assert.Single(fired);

        session.Flush(at + TimeSpan.FromSeconds(3));
        Assert.Equal([1, 1], fired.Select(a => a.Count));
    }

    [Fact]
    public async Task Alert_sessions_unattended_for_exactly_KeepFor_are_kept_and_let_go_after()
    {
        var time = new FakeTimeProvider(WholeSecond());
        var host = new BlotterHost(new BlotterOptions { Time = time, Stepped = true }, NullLogger<BlotterHost>.Instance);
        using var desk = new AlertDesk(host);
        var session = desk.Open("browser");
        session.Attach();
        session.Detach();

        time.Advance(AlertDesk.KeepFor);
        Assert.Same(session, desk.Open("browser"));

        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.NotSame(session, desk.Open("browser"));
        await host.DisposeAsync();
    }

    // ---- 12. Ingestion ----

    [Fact]
    public void Ingestion_writes_a_row_on_its_due_instant_and_a_stall_holds_it_until_the_stall_ends()
    {
        var time = new FakeTimeProvider(WholeSecond());
        var written = new List<Fill>();
        var writer = new IngestWriter<Fill>(
            rows => written.AddRange(rows),
            new IngestLag { Min = TimeSpan.FromSeconds(2), Max = TimeSpan.FromSeconds(2), StallEvery = TimeSpan.Zero }, 1, time);
        var (first, second) = (Fill(time.GetUtcNow().UtcDateTime), Fill(time.GetUtcNow().UtcDateTime));
        writer.Enqueue([first]);

        time.Advance(TimeSpan.FromSeconds(2) - Micro);
        writer.WriteDue();
        Assert.Empty(written);

        time.Advance(Micro);
        writer.WriteDue();
        Assert.Equal([first], written);

        writer.Enqueue([second]);
        writer.StallUntil(time.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5) - Micro);
        writer.WriteDue();
        Assert.Equal([first], written);

        time.Advance(Micro);
        writer.WriteDue();
        Assert.Equal([first, second], written);
    }

    // ---- The harness ----

    private static DateTimeOffset WholeSecond() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static async Task<SteppedHouse> SteppedAsync(
        CurrentSync sync, double written, double current = 3, TimeSpan? gapFill = null, double newShare = 0.3,
        TimeSpan? confirmEvery = null, TimeSpan? clockEvery = null, TimeSpan? haircut = null)
    {
        var given = Environment.GetEnvironmentVariable("CHALK_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(given) && !PgServer.CanStart())
        {
            Assert.Skip("no PostgreSQL: install PostgreSQL 16 or later, or set CHALK_TEST_POSTGRES to a connection string");
        }

        var time = new FakeTimeProvider(WholeSecond());
        IngestLag Fixed(double seconds) => new()
        {
            Min = TimeSpan.FromSeconds(seconds), Max = TimeSpan.FromSeconds(seconds), StallEvery = TimeSpan.Zero,
        };
        var options = new BlotterOptions
        {
            Postgres = given,
            Orders = 5_000,
            BudgetBytes = 64L << 20,
            FindBudget = false,
            Current = sync,
            Time = time,
            Stepped = true,
            GapFill = gapFill ?? TimeSpan.FromSeconds(30),
            GapFillHaircut = haircut ?? TimeSpan.Zero,
            ConfirmEvery = confirmEvery ?? TimeSpan.FromMilliseconds(500),
            ClockEvery = clockEvery ?? TimeSpan.FromMilliseconds(250),
            Exchange = new ExchangeOptions
            {
                BurstEvery = TimeSpan.Zero, OrderSkew = TimeSpan.Zero, NewShare = newShare,
                Lag = Fixed(written), CurrentLag = Fixed(current),
            },
        };
        var host = new BlotterHost(options, NullLogger<BlotterHost>.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        // The corpus runs up to the instant the clock started, and the house never sends a version
        // timed before its last: a second on, every version it makes is timed by the clock.
        time.Advance(TimeSpan.FromSeconds(1));
        return new SteppedHouse(host, time);
    }

    /// <summary>A host and its house on one fake clock, turned by hand.</summary>
    private sealed class SteppedHouse(BlotterHost host, FakeTimeProvider time) : IAsyncDisposable
    {
        public BlotterHost Host => host;

        public DateTime Now => time.GetUtcNow().UtcDateTime;

        /// <summary>
        /// Changes made now, and sent: the house times each change a microsecond after the last, so the
        /// clock moves on to the last of them, and the house lets them go. The order versions, oldest first.
        /// </summary>
        public Order[] Send(int changes)
        {
            var (orders, fills) = host.Exchange.Send(changes);
            var last = orders.Select(o => o.EventTime).Concat(fills.Select(f => f.EventTime)).DefaultIfEmpty(Now).Max();
            if (last > Now)
            {
                time.SetUtcNow(new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)));
            }

            host.Exchange.Turn();
            return orders;
        }

        /// <summary>
        /// Goes live now: subscribes, waits out the haircut while the house writes what falls due, and
        /// reads the one gap fill at its end.
        /// </summary>
        public async Task GoLiveAsync()
        {
            var live = host.GoLiveAsync(CancellationToken.None);
            if (host.Options.GapFillHaircut > TimeSpan.Zero)
            {
                var end = time.GetUtcNow() + host.Options.GapFillHaircut;
                time.SetUtcNow(end - Micro);
                host.Exchange.Turn();
                time.SetUtcNow(end);
            }

            await live;
        }

        /// <summary>The house does what is due now, and then the host takes a turn.</summary>
        public async Task TurnAsync()
        {
            host.Exchange.Turn();
            await host.TurnAsync();
        }

        /// <summary>Moves the clock to <paramref name="at" />, never back, and turns.</summary>
        public async Task AtAsync(DateTime at)
        {
            var to = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
            Assert.True(to >= time.GetUtcNow(), $"the clock cannot go back to {at:O}");
            time.SetUtcNow(to);
            await TurnAsync();
        }

        /// <summary>Runs <paramref name="work" />, which waits on the catch-up loop, turning the loop until it is done.</summary>
        public async Task<T> OnLoopAsync<T>(Task<T> work)
        {
            for (var turns = 0; !work.IsCompleted; turns++)
            {
                Assert.True(turns < 1000, "the catch-up loop never took the work");
                await host.TurnAsync();
                await Task.Delay(1);
            }

            return await work;
        }

        public Task<LiveScope> AcquireAsync(Tab tab, FilterSpec filter) => OnLoopAsync(host.AcquireAsync(tab, filter, CancellationToken.None));

        public Task ReleaseAsync(LiveScope scope) => OnLoopAsync(host.ReleaseAsync(scope).ContinueWith(_ => true, TaskScheduler.Default));

        /// <summary>
        /// What differs between the orders' rows the host shows now and the oracle's, or null when they agree:
        /// each at the newest version the database or the streams have.
        /// </summary>
        public async Task<string?> ShownAsync(IEnumerable<Order> versions)
        {
            var ids = versions.Select(v => v.OrderId.ToString()).Distinct().ToArray();
            var filter = FilterSpec.Of([new ColumnFilter { Column = Tabs.Orders.Named("order_id"), Values = [.. ids] }]);
            var scope = await AcquireAsync(Tabs.Orders, filter);
            try
            {
                var order = Tabs.Orders.DefaultSort;
                var page = await host.Pages.View(order).GetAsync(scope.State, 0, ids.Length + 10, CancellationToken.None);
                var (expected, _) = Oracle.From(host.Seeded, host.Exchange, scope.State, order, null, ids.Length + 10);
                return Oracle.Compare(page.Rows, expected);
            }
            finally
            {
                await ReleaseAsync(scope);
            }
        }

        /// <summary>One refresh, which sets the engine's clock, as the market running would.</summary>
        public async Task ClockSetAsync()
        {
            Send(1);
            await TurnAsync();
        }

        /// <summary>A browser with <paramref name="rules" />, their views open, and what they fire.</summary>
        public async Task<(AlertSession Session, List<AlertEvent> Fired)> AlertsAsync(params AlertRule[] rules)
        {
            _desk ??= new AlertDesk(host);
            var session = _desk.Open("browser");
            var fired = new List<AlertEvent>();
            session.Fired += alert =>
            {
                lock (fired)
                {
                    fired.Add(alert);
                }
            };
            session.SetRules(rules);

            // The desk asks for the views on its timer; the loop opens them on its next turn.
            time.Advance(TimeSpan.FromMilliseconds(250));
            await TurnAsync();
            await TurnAsync();
            Assert.True(host.AlertsRefused.IsEmpty, string.Join("\n", host.AlertsRefused.Values));
            Assert.True(host.AlertViewsOpen > 0, "no alert view opened");
            return (session, fired);
        }

        private AlertDesk? _desk;

        public async ValueTask DisposeAsync()
        {
            _desk?.Dispose();
            await host.DisposeAsync();
        }
    }

    private static AlertRule Rule(string id, Tab tab, string condition, TimeSpan? lagOver = null, AlertCount? count = null) =>
        new(id, id, Condition.Parse(tab, condition), AlertSounds.Default, TimeSpan.FromSeconds(1), false, lagOver, count);

    private static Order Order(int version, DateTime at) =>
        new(Guid.CreateVersion7(), version, at, at, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 2, 0, 0, 0, 0, "New");

    private static Fill Fill(DateTime at) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), at, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 1, 100, 100, 100, 0);
}
