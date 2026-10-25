using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Alerts;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// A browser's alerts: which rules the rows entering a view fire, how a throttle gathers them, and how
/// they wait to be sounded. What enters a view, ChalkQL decides: the host's tests show that.
/// </summary>
public sealed class AlertSessionTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0);

    [Fact]
    public void Rows_entering_a_view_fire_the_rules_that_watch_it_and_no_others()
    {
        var cedar = Rule(Tabs.Orders, ("client", "CEDAR"));
        var (session, fired) = Session(cedar);
        session.SetRules([cedar, Rule(Tabs.Orders, ("client", "FERN")) with { Id = "fern" }]);

        session.Enter(View(cedar), [(Order(Guid.CreateVersion7(), 2, "Partial"), null)], Now);

        Assert.Equal([("rule", 1)], fired.Select(a => (a.RuleId, a.Count)));
        Assert.Contains("CEDAR Buy 2 BTC/USD Perp · Partial", fired[0].Latest, StringComparison.Ordinal);
    }

    [Fact]
    public void Within_its_throttle_a_rule_gathers_its_rows_and_sounds_once_saying_how_many()
    {
        var rule = Rule(Tabs.Fills, ("client", "CEDAR")) with { Throttle = TimeSpan.FromSeconds(3) };
        var (session, fired) = Session(rule);

        session.Enter(View(rule), [(Fill("CEDAR"), null)], Now);
        session.Enter(View(rule), [(Fill("CEDAR"), null), (Fill("CEDAR"), null)], Now.AddSeconds(1));
        session.Flush(Now.AddSeconds(2));
        Assert.Single(fired);

        session.Flush(Now.AddSeconds(3));
        Assert.Equal([1, 2], fired.Select(a => a.Count));
        Assert.Equal([1L, 2L], fired.Select(a => a.Seq));
    }

    [Fact]
    public void Alerts_wait_in_order_until_a_page_has_sounded_them()
    {
        var rule = Rule(Tabs.Fills);
        var (session, _) = Session(rule);
        session.Enter(View(rule), [(Fill("CEDAR"), null)], Now);
        session.Enter(View(rule), [(Fill("FERN"), null)], Now.AddSeconds(5));

        Assert.Equal([1L, 2L], session.Waiting().Select(a => a.Seq));
        session.Acknowledge(1);
        Assert.Equal([2L], session.Waiting().Select(a => a.Seq));
    }

    [Fact]
    public void A_rule_whose_condition_is_unchanged_keeps_what_it_gathered_and_a_changed_one_starts_again()
    {
        var rule = Rule(Tabs.Fills, ("client", "CEDAR")) with { Throttle = TimeSpan.FromSeconds(10) };
        var (session, fired) = Session(rule);
        session.Enter(View(rule), [(Fill("CEDAR"), null)], Now);
        session.Enter(View(rule), [(Fill("CEDAR"), null)], Now.AddSeconds(1));

        session.SetRules([rule with { Name = "renamed", Sound = "laserfire" }]);
        session.Flush(Now.AddSeconds(10));
        Assert.Equal(("renamed", "laserfire", 1), (fired[^1].RuleName, fired[^1].Sound, fired[^1].Count));

        session.Enter(View(rule), [(Fill("CEDAR"), null)], Now.AddSeconds(11));
        session.SetRules([Rule(Tabs.Fills, ("client", "FERN")) with { Id = rule.Id }]);
        session.Flush(Now.AddSeconds(30));
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void A_rule_that_watches_the_wait_has_a_view_of_its_own_and_says_how_long_the_row_waited()
    {
        var arriving = Rule(Tabs.Orders, ("client", "CEDAR"));
        var waiting = arriving with { Id = "waiting", LagOver = TimeSpan.FromSeconds(2) };
        var (session, fired) = Session(arriving);
        session.SetRules([arriving, waiting]);
        Assert.NotEqual(View(arriving), View(waiting));

        session.Enter(View(waiting), [(Order(Guid.CreateVersion7(), 1, "New"), TimeSpan.FromSeconds(3))], Now);

        Assert.Equal(["waiting"], fired.Select(a => a.RuleId));
        Assert.Contains("3.0\u202Fs waiting for the database", fired[0].Latest, StringComparison.Ordinal);
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

    [Fact]
    public void A_count_reads_back_the_same_and_one_out_of_range_or_with_a_wait_is_refused()
    {
        var rule = Rule(Tabs.Orders, ("client", "CEDAR")) with { Count = new AlertCount(CountKind.AtLeast, 5, TimeSpan.FromSeconds(90)) };
        Assert.Equal(rule.Count, AlertRule.Load(rule.Save(), Tabs.All).Count);
        var none = rule with { Count = new AlertCount(CountKind.None, 0, TimeSpan.FromMinutes(15)) };
        Assert.Equal(none.Count, AlertRule.Load(none.Save(), Tabs.All).Count);
        Assert.Equal("none in 15 min", none.Count!.Describe());
        Assert.Equal("at least 5 in 90 s", rule.Count!.Describe());

        Assert.Equal("a window runs from 5 s to 60 min", (rule.Count with { Window = TimeSpan.FromSeconds(4) }).Problem());
        Assert.Equal("a window runs from 5 s to 60 min", (rule.Count with { Window = TimeSpan.FromMinutes(61) }).Problem());
        Assert.Equal("at least runs from 1 to 1,000,000 matches", (rule.Count with { Matches = 0 }).Problem());
        Assert.Null((rule.Count with { Kind = CountKind.None, Matches = 0 }).Problem());
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { WindowSeconds = 3601 }, Tabs.All));
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { CountKind = "most" }, Tabs.All));
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { LagOverSeconds = 2 }, Tabs.All));
    }

    [Fact]
    public void A_count_view_entering_fires_the_rules_that_count_with_it_at_once_saying_how_many()
    {
        var rule = Rule(Tabs.Fills, ("client", "CEDAR")) with
        {
            Throttle = TimeSpan.FromSeconds(30), Count = new AlertCount(CountKind.AtLeast, 5, TimeSpan.FromMinutes(1)),
        };
        var (session, fired) = Session(rule);
        var window = AlertWindowSpec.Of(rule)!;
        Assert.Equal(TimeSpan.FromSeconds(1), window.Slice);

        // The alert view the count reads fires nothing itself; the count view does, throttle or none.
        session.Enter(View(rule), [(Fill("CEDAR"), null)], Now);
        session.Count(window.Name, 7, Now);
        session.Count(window.Name, 5, Now.AddSeconds(1));
        Assert.Equal(["7 in the last 1 min", "5 in the last 1 min"], fired.Select(a => a.Latest));

        var none = rule with { Count = new AlertCount(CountKind.None, 0, TimeSpan.FromMinutes(30)) };
        session.SetRules([none]);
        Assert.Equal(TimeSpan.FromSeconds(30), AlertWindowSpec.Of(none)!.Slice);
        session.Count(AlertWindowSpec.Of(none)!.Name, 0, Now);
        Assert.Equal("none in the last 30 min", fired[^1].Latest);
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

    private static string View(AlertRule rule) => AlertViewSpec.Of(rule).Name;

    private static Order Order(Guid id, int version, string status) =>
        new(id, version, Now, Now, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 2, 0, 0, 0, 0, status);

    private static Fill Fill(string client) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, client + "-1", client, "EMEA", "API", "BTC/USD Perp", "Buy", 1, 100, 100, 100, 0);
}
