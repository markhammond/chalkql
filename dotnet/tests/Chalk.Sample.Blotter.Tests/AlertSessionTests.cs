using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A browser's alerts: what fires them, how a throttle gathers them, and how they wait to be sounded.</summary>
public sealed class AlertSessionTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0);

    [Fact]
    public void An_order_fires_as_it_comes_to_pass_the_filter_and_not_again_until_it_has_left_it()
    {
        var (session, fired) = Session(Rule(Tab.Orders, ("status", "Rejected")));
        var id = Guid.CreateVersion7();

        session.Note([Order(id, 1, "New")], [], Now);
        session.Note([Order(id, 2, "Rejected")], [], Now.AddSeconds(10));
        session.Note([Order(id, 3, "Rejected")], [], Now.AddSeconds(20));
        session.Note([Order(id, 4, "New"), Order(id, 5, "Rejected")], [], Now.AddSeconds(30));

        Assert.Equal([1, 1], fired.Select(a => a.Count));
        Assert.Contains("CEDAR Buy 2 BTC/USD Perp · Rejected", fired[0].Latest, StringComparison.Ordinal);
    }

    [Fact]
    public void Within_its_throttle_a_rule_gathers_its_rows_and_sounds_once_saying_how_many()
    {
        var (session, fired) = Session(Rule(Tab.Fills, ("client", "CEDAR")) with { Throttle = TimeSpan.FromSeconds(3) });

        session.Note([], [Fill("CEDAR")], Now);
        session.Note([], [Fill("CEDAR"), Fill("FERN"), Fill("CEDAR")], Now.AddSeconds(1));
        session.Flush(Now.AddSeconds(2));
        Assert.Single(fired);

        session.Flush(Now.AddSeconds(3));
        Assert.Equal([1, 2], fired.Select(a => a.Count));
        Assert.Equal([1L, 2L], fired.Select(a => a.Seq));
    }

    [Fact]
    public void Alerts_wait_in_order_until_a_page_has_sounded_them()
    {
        var (session, _) = Session(Rule(Tab.Fills));
        session.Note([], [Fill("CEDAR")], Now);
        session.Note([], [Fill("FERN")], Now.AddSeconds(5));

        Assert.Equal([1L, 2L], session.Waiting().Select(a => a.Seq));
        session.Acknowledge(1);
        Assert.Equal([2L], session.Waiting().Select(a => a.Seq));
    }

    [Fact]
    public void A_rule_whose_filter_is_unchanged_keeps_what_it_gathered_and_a_changed_one_starts_again()
    {
        var rule = Rule(Tab.Fills, ("client", "CEDAR")) with { Throttle = TimeSpan.FromSeconds(10) };
        var (session, fired) = Session(rule);
        session.Note([], [Fill("CEDAR")], Now);
        session.Note([], [Fill("CEDAR")], Now.AddSeconds(1));

        session.SetRules([rule with { Name = "renamed", Sound = AlertSound.Laser }]);
        session.Flush(Now.AddSeconds(10));
        Assert.Equal(("renamed", AlertSound.Laser, 1), (fired[^1].RuleName, fired[^1].Sound, fired[^1].Count));

        session.Note([], [Fill("CEDAR")], Now.AddSeconds(11));
        session.SetRules([Rule(Tab.Fills, ("client", "FERN")) with { Id = rule.Id }]);
        session.Flush(Now.AddSeconds(30));
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void A_rule_kept_by_the_browser_reads_back_the_same_and_one_not_understood_is_refused()
    {
        var rule = Rule(Tab.Fills, ("client", "CEDAR")) with { Sound = AlertSound.Zap, Throttle = TimeSpan.FromSeconds(30), Muted = true };
        var back = AlertRule.Load(rule.Save());

        Assert.Equal((rule.Tab, rule.Filter.Name, rule.Sound, rule.Throttle, rule.Muted), (back.Tab, back.Filter.Name, back.Sound, back.Throttle, back.Muted));
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { Sound = "Gong" }));
        Assert.Throws<ArgumentException>(() => AlertRule.Load(rule.Save() with { View = "sort=-client" }));
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
        "rule", "a rule", tab,
        FilterSpec.Of(filters.Select(f => new ColumnFilter { Column = tab.Named(f.Column), Values = [f.Value] })),
        AlertSound.Register, TimeSpan.FromSeconds(1), false);

    private static Order Order(Guid id, int version, string status) =>
        new(id, version, Now, Now, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 2, 0, 0, 0, status);

    private static Fill Fill(string client) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, client + "-1", client, "EMEA", "API", "BTC/USD Perp", "Buy", 1, 100, 100, 0);
}
