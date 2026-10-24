using System.Collections.Immutable;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Grid;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
/// Alert conditions in SQL: the names they read, along paths too, what they may not hold, how the
/// statement that runs them names things, and what the editor offers at the cursor. Whether ChalkQL
/// takes a condition, and what it finds, the host's tests show.
/// </summary>
public sealed class ConditionTests
{
    [Fact]
    public void A_condition_reads_its_tabs_columns_and_along_its_paths_whatever_their_case()
    {
        var condition = Condition.Parse(Tabs.Fills, "order.Status = 'Partial' AND QTY >= 10 AND lower(client) LIKE 'ced%'");

        Assert.Empty(condition.Errors);
        Assert.Equal(["order.status", "qty", "client"], condition.Names.Select(n => (n.Path is null ? "" : n.Path.Name + ".") + n.Column.Name));
        Assert.Equal("order.Status", condition.Text[condition.Names[0].Span.From..condition.Names[0].Span.To]);
        Assert.Equal(["order"], condition.Paths.Select(p => p.Name));
    }

    [Fact]
    public void A_name_it_does_not_know_is_refused_where_it_is()
    {
        var text = "qty >= 1 AND colour = 'red' AND order.nope = 1 AND trade.qty = 1";
        var condition = Condition.Parse(Tabs.Fills, text);

        Assert.Equal(
            ["Fills has no column 'colour'", "Client orders has no column 'nope'", "Fills has no path 'trade'; it has order."],
            condition.Errors.Select(e => e.Message));
        Assert.Equal("colour", text[condition.Errors[0].Span.From..condition.Errors[0].Span.To]);
        Assert.Equal("nope", text[condition.Errors[1].Span.From..condition.Errors[1].Span.To]);
        Assert.Contains("worked out and shown", Condition.Parse(Tabs.Orders, "filled_pct > 50").Errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("no path 'order'", Condition.Parse(Tabs.Orders, "order.qty > 1").Errors[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qty > 1; DROP TABLE x", "one condition, no ';'")]
    [InlineData("EXISTS (SELECT 1 FROM blotter.fills)", "'EXISTS' has no place in one")]
    [InlineData("qty > 1) UNION SELECT (1", "a ')' with no '(' before it")]
    [InlineData("qty > @limit", "a condition takes no parameters: write the value")]
    [InlineData("qty > 1 -- and more", "a condition takes no comments")]
    [InlineData("(qty > 1", "a '(' is not closed")]
    [InlineData("client = 'CEDAR", "this text has no closing quote")]
    public void What_is_not_a_condition_is_refused(string text, string refusal)
    {
        Assert.Contains(Condition.Parse(Tabs.Orders, text).Errors, e => e.Message.Contains(refusal, StringComparison.Ordinal));
    }

    [Fact]
    public void The_statement_names_the_columns_as_it_reads_them_and_leaves_the_rest_as_written()
    {
        var condition = Condition.Parse(Tabs.Fills, "order.status IN ('New', 'Partial') AND side = 'Buy' AND \"qty\" > 1e3");

        Assert.Equal(
            "o(status) IN ('New', 'Partial') AND a.side = 'Buy' AND a.qty > 1e3",
            condition.Sql(c => "a." + c.Name, (p, c) => $"o({c.Name})"));
        Assert.Equal("TRUE", Condition.Parse(Tabs.Fills, "  ").Sql(c => c.Name, (p, c) => c.Name));
        Assert.Throws<InvalidOperationException>(() => Condition.Parse(Tabs.Fills, "nope = 1").Sql(c => c.Name, (p, c) => c.Name));
    }

    [Fact]
    public void The_funnels_filters_read_as_a_condition_with_their_values_written_in()
    {
        var filter = FilterSpec.Of(
        [
            new ColumnFilter { Column = Tabs.Orders.Named("client"), Values = ["CEDAR", "O'NEIL"] },
            new ColumnFilter { Column = Tabs.Orders.Named("instrument"), Values = ["BTC", "A_B"], Match = TextMatch.StartsWith },
            new ColumnFilter { Column = Tabs.Orders.Named("qty"), Min = 10 },
            new ColumnFilter { Column = Tabs.Orders.Named("version"), Max = 3 },
            new ColumnFilter { Column = Tabs.Orders.Named("order_time"), From = new DateTime(2026, 10, 24, 9, 0, 0) },
            new ColumnFilter { Column = Tabs.Orders.Named("order_id"), Values = ["01a16d70-0000-7000-8000-000000000001"] },
        ]);

        var text = filter.Condition();
        Assert.Equal(
            "client IN ('CEDAR', 'O''NEIL') AND (instrument LIKE 'A\\_B%' ESCAPE '\\' OR instrument LIKE 'BTC%') "
            + "AND order_id = CAST('01a16d70-0000-7000-8000-000000000001' AS UUID) "
            + "AND order_time >= TIMESTAMP '2026-10-24 09:00:00.000000' AND qty >= 10 AND version <= 3",
            text);
        Assert.Empty(Condition.Parse(Tabs.Orders, text).Errors);
        Assert.Equal("", FilterSpec.None.Condition());
    }

    [Fact]
    public async Task Where_a_comparison_starts_the_editor_offers_columns_and_paths_and_after_a_dot_the_paths_columns()
    {
        var start = await Complete(Tabs.Fills, "");
        Assert.Contains("qty", start.Options.Select(o => o.Label));
        Assert.Contains(start.Options, o => o is { Label: "order", Type: "namespace", Apply: "order." });
        Assert.DoesNotContain("filled_pct", start.Options.Select(o => o.Label));

        var along = await Complete(Tabs.Fills, "side = 'Buy' AND order.st");
        Assert.Equal(["status"], along.Options.Select(o => o.Label));
        Assert.Equal("side = 'Buy' AND order.".Length, along.From);

        var typing = await Complete(Tabs.Orders, "cl");
        Assert.Equal(["cl_ord_id", "client"], typing.Options.Select(o => o.Label));
    }

    [Fact]
    public async Task After_a_name_the_editor_offers_its_operators_then_its_values_then_AND_and_OR()
    {
        Assert.Contains(">=", (await Complete(Tabs.Orders, "qty ")).Options.Select(o => o.Label));
        Assert.Contains("LIKE", (await Complete(Tabs.Orders, "client ")).Options.Select(o => o.Label));
        Assert.DoesNotContain("LIKE", (await Complete(Tabs.Orders, "qty ")).Options.Select(o => o.Label));

        var choices = await Complete(Tabs.Fills, "order.status = ");
        Assert.Equal(Reference.Statuses, choices.Options.Select(o => o.Label));
        Assert.Equal("'Partial'", choices.Options.Single(o => o.Label == "Partial").Apply);

        // Inside a quote, a choice's values; a text column's, from the host; and the quote closed.
        var quoted = await Complete(Tabs.Orders, "side IN ('Buy', 'S");
        Assert.Equal([("Sell", "Sell'")], quoted.Options.Select(o => (o.Label, o.Apply)));
        var looked = await Complete(Tabs.Orders, "client = 'CE", values: ["CEDAR"]);
        Assert.Equal([("CEDAR", "CEDAR'")], looked.Options.Select(o => (o.Label, o.Apply)));

        Assert.Equal(["AND", "OR"], (await Complete(Tabs.Orders, "qty >= 10 ")).Options.Select(o => o.Label));
    }

    private static Task<Suggestions> Complete(Tab tab, string text, string[]? values = null) =>
        ConditionCompletion.CompleteAsync(
            tab, text, text.Length, (_, typed, _) => Task.FromResult<IReadOnlyList<string>>([.. (values ?? []).Where(v => v.StartsWith(typed, StringComparison.Ordinal))]),
            CancellationToken.None);
}
