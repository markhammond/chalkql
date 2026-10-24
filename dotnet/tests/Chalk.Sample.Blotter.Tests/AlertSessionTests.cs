using System.Collections.Immutable;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A browser's alerts: what fires them, how a throttle gathers them, and how they wait to be sounded.</summary>
public sealed class AlertSessionTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0);

    [Fact]
    public void An_order_fires_as_it_comes_to_pass_the_filter_and_not_again_until_it_has_left_it()
    {
        var (session, fired) = Session(Rule(Tabs.Orders, ("status", "Rejected")));
        var id = Guid.CreateVersion7();

        session.Note([Order(id, 1, "New")], [], Passes, Now);
        session.Note([Order(id, 2, "Rejected")], [], Passes, Now.AddSeconds(10));
        session.Note([Order(id, 3, "Rejected")], [], Passes, Now.AddSeconds(20));
        session.Note([Order(id, 4, "New"), Order(id, 5, "Rejected")], [], Passes, Now.AddSeconds(30));

        Assert.Equal([1, 1], fired.Select(a => a.Count));
        Assert.Contains("CEDAR Buy 2 BTC/USD Perp · Rejected", fired[0].Latest, StringComparison.Ordinal);
    }

    [Fact]
    public void Within_its_throttle_a_rule_gathers_its_rows_and_sounds_once_saying_how_many()
    {
        var (session, fired) = Session(Rule(Tabs.Fills, ("client", "CEDAR")) with { Throttle = TimeSpan.FromSeconds(3) });

        session.Note([], [Fill("CEDAR")], Passes, Now);
        session.Note([], [Fill("CEDAR"), Fill("FERN"), Fill("CEDAR")], Passes, Now.AddSeconds(1));
        session.Flush(Now.AddSeconds(2));
        Assert.Single(fired);

        session.Flush(Now.AddSeconds(3));
        Assert.Equal([1, 2], fired.Select(a => a.Count));
        Assert.Equal([1L, 2L], fired.Select(a => a.Seq));
    }

    [Fact]
    public void Alerts_wait_in_order_until_a_page_has_sounded_them()
    {
        var (session, _) = Session(Rule(Tabs.Fills));
        session.Note([], [Fill("CEDAR")], Passes, Now);
        session.Note([], [Fill("FERN")], Passes, Now.AddSeconds(5));

        Assert.Equal([1L, 2L], session.Waiting().Select(a => a.Seq));
        session.Acknowledge(1);
        Assert.Equal([2L], session.Waiting().Select(a => a.Seq));
    }

    [Fact]
    public void A_rule_whose_filter_is_unchanged_keeps_what_it_gathered_and_a_changed_one_starts_again()
    {
        var rule = Rule(Tabs.Fills, ("client", "CEDAR")) with { Throttle = TimeSpan.FromSeconds(10) };
        var (session, fired) = Session(rule);
        session.Note([], [Fill("CEDAR")], Passes, Now);
        session.Note([], [Fill("CEDAR")], Passes, Now.AddSeconds(1));

        session.SetRules([rule with { Name = "renamed", Sound = "laserfire" }]);
        session.Flush(Now.AddSeconds(10));
        Assert.Equal(("renamed", "laserfire", 1), (fired[^1].RuleName, fired[^1].Sound, fired[^1].Count));

        session.Note([], [Fill("CEDAR")], Passes, Now.AddSeconds(11));
        session.SetRules([Rule(Tabs.Fills, ("client", "FERN")) with { Id = rule.Id }]);
        session.Flush(Now.AddSeconds(30));
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void A_rule_that_watches_the_wait_fires_once_for_each_row_that_waits_too_long_for_the_database()
    {
        var (session, fired) = Session(Rule(Tabs.Orders, ("client", "CEDAR")) with { LagOver = TimeSpan.FromSeconds(2) });
        var slow = Order(Guid.CreateVersion7(), 1, "New");
        var quick = Order(Guid.CreateVersion7(), 1, "New");
        var other = Order(Guid.CreateVersion7(), 1, "New") with { Client = "FERN" };
        var tail = ImmutableDictionary<Guid, Reconciliation>.Empty
            .Add(slow.OrderId, new Reconciliation(slow, Now, null))
            .Add(quick.OrderId, new Reconciliation(quick, Now, Now.AddSeconds(1)))
            .Add(other.OrderId, new Reconciliation(other, Now, null));

        // What arrives does not fire it: only the wait does.
        session.Note([slow, quick, other], [], Passes, Now);
        session.Weigh(tail, Passes, Now.AddSeconds(1));
        Assert.Empty(fired);

        session.Weigh(tail, Passes, Now.AddSeconds(3));
        session.Weigh(tail, Passes, Now.AddSeconds(10));
        Assert.Equal([1], fired.Select(a => a.Count));
        Assert.Contains("3.0\u202Fs waiting for the database", fired[0].Latest, StringComparison.Ordinal);

        // A new version starts a new wait, which may fire again.
        var next = slow with { Version = 2 };
        session.Weigh(tail.SetItem(slow.OrderId, new Reconciliation(next, Now.AddSeconds(10), null)), Passes, Now.AddSeconds(13));
        Assert.Equal([1, 1], fired.Select(a => a.Count));
    }

    [Fact]
    public void A_rule_kept_by_the_browser_reads_back_the_same_and_one_not_understood_is_refused()
    {
        var rule = Rule(Tabs.Fills, ("client", "CEDAR")) with
        {
            Sound = "bangecho", Throttle = TimeSpan.FromSeconds(30), Muted = true, LagOver = TimeSpan.FromSeconds(5),
        };
        var back = AlertRule.Load(rule.Save(), Tabs.All);

        Assert.Equal(
            (rule.Tab, rule.Condition.Text, rule.Sound, rule.Throttle, rule.Muted, rule.LagOver),
            (back.Tab, back.Condition.Text, back.Sound, back.Throttle, back.Muted, back.LagOver));
        Assert.Null(AlertRule.Load(rule.Save() with { LagOverSeconds = null }, Tabs.All).LagOver);
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { LagOverSeconds = 7 }, Tabs.All));
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { Sound = "Gong" }, Tabs.All));
        Assert.Equal("kaching", AlertRule.Load(rule.Save() with { Sound = "Register" }, Tabs.All).Sound);
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { Condition = "nope = 1" }, Tabs.All));

        // Kept before conditions were SQL: a filter, as a view's link, read as the condition it means.
        var kept = AlertRule.Load(rule.Save() with { Condition = null, Tab = null, View = "tab=fills&f.client=CEDAR" }, Tabs.All);
        Assert.Equal((Tabs.Fills, "client = 'CEDAR'"), (kept.Tab, kept.Condition.Text));
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { Condition = null, View = "sort=-client" }, Tabs.All));
    }

    private static (AlertSession Session, List<AlertEvent> Fired) Session(AlertRule rule)
    {
        var session = new AlertSession("browser");
        var fired = new List<AlertEvent>();
        session.Fired += fired.Add;
        session.SetRules([rule]);
        return (session, fired);
    }

    private static AlertRule Rule(Tab tab, params (string Column, string Value)[] filters) => new(
        "rule", "a rule", Condition.Parse(tab, string.Join(" AND ", filters.Select(f => $"{f.Column} = '{f.Value}'"))),
        AlertSounds.Default, TimeSpan.FromSeconds(1), false);

    /// <summary>
    /// Whether a row passes a rule's condition, as ChalkQL would decide for the conditions these tests
    /// write: columns equal to text. The host's tests decide it with ChalkQL itself.
    /// </summary>
    private static bool Passes(AlertRule rule, IRow row) =>
        rule.Condition.IsEmpty || rule.Condition.Text.Split(" AND ").All(part =>
        {
            var (column, value) = (part[..part.IndexOf(" = ", StringComparison.Ordinal)], part[(part.IndexOf('\'') + 1)..^1]);
            return rule.Tab.Named(column).Value(row).ToString() == value;
        });

    private static Order Order(Guid id, int version, string status) =>
        new(id, version, Now, Now, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 2, 0, 0, 0, status);

    private static Fill Fill(string client) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, client + "-1", client, "EMEA", "API", "BTC/USD Perp", "Buy", 1, 100, 100, 0);
}
