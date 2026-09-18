using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The D27 / D29 rewrite, tested as the pure function it is — no planner, no sidecar. Calcite knows
/// only positional <c>?</c> (V13), so everything a host may write about parameters is decided here.
/// </summary>
public sealed class ParameterRewriterTests
{
    private static string Render(string sql)
    {
        var rewriter = ParameterRewriter.Parse(sql);
        return rewriter.Render(rewriter.PrepareShape()).Sql;
    }

    // ---- styles ----

    [Fact]
    public void Positional_parameters_pass_through_and_each_one_is_its_own()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol = ? AND ts >= ?");

        Assert.Equal(ParameterStyle.Positional, rewriter.Style);
        Assert.Equal(2, rewriter.Parameters.Count);
        Assert.Equal("SELECT * FROM bars WHERE symbol = ? AND ts >= ?", Render(rewriter));
    }

    [Fact]
    public void Named_parameters_become_question_marks_in_order()
    {
        var rewriter = ParameterRewriter.Parse(
            "SELECT * FROM bars WHERE symbol = @symbol AND ts >= @from AND ts < @to");

        Assert.Equal(ParameterStyle.Named, rewriter.Style);
        Assert.Equal(["symbol", "from", "to"], rewriter.Parameters.Select(p => p.Name));
        Assert.Equal(
            "SELECT * FROM bars WHERE symbol = ? AND ts >= ? AND ts < ?",
            Render(rewriter));
    }

    [Fact]
    public void A_recurring_named_parameter_is_one_logical_parameter_bound_once()
    {
        var rewriter = ParameterRewriter.Parse(
            "SELECT * FROM bars WHERE ts >= @since AND (trade_count IS NULL OR ts < @since)");

        Assert.Single(rewriter.Parameters);
        Assert.Equal(2, rewriter.Parameters[0].Occurrences.Count);
        Assert.Equal(2, rewriter.OccurrenceCount);
    }

    [Fact]
    public void A_recurring_ordinal_is_one_logical_parameter_bound_once()
    {
        var rewriter = ParameterRewriter.Parse(
            "SELECT * FROM bars WHERE ts >= $1 AND symbol = $2 AND ts < $1");

        Assert.Equal(ParameterStyle.Ordinal, rewriter.Style);
        Assert.Equal(2, rewriter.Parameters.Count);
        Assert.Equal([1, 2], rewriter.Parameters.Select(p => p.Ordinal));
        Assert.Equal(
            "SELECT * FROM bars WHERE ts >= ? AND symbol = ? AND ts < ?",
            Render(rewriter));
    }

    [Fact]
    public void Named_parameters_are_case_insensitive()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE a = @Since AND b = @since");

        Assert.Single(rewriter.Parameters);
    }

    [Fact]
    public void A_statement_with_no_parameters_has_style_none()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars");

        Assert.Equal(ParameterStyle.None, rewriter.Style);
        Assert.Empty(rewriter.Parameters);
    }

    // ---- negatives ----

    [Fact]
    public void Mixing_styles_names_both_offenders()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ParameterRewriter.Parse("SELECT * FROM bars WHERE a = ? AND b = @name"));

        Assert.Contains("mixes parameter styles", error.Message, StringComparison.Ordinal);
        Assert.Contains("?", error.Message, StringComparison.Ordinal);
        Assert.Contains("@name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinal_gap_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(
            () => ParameterRewriter.Parse("SELECT * FROM bars WHERE a = $1 AND b = $3"));

        Assert.Contains("$3", error.Message, StringComparison.Ordinal);
        Assert.Contains("$2", error.Message, StringComparison.Ordinal);
    }

    // ---- what is not a parameter ----

    [Fact]
    public void A_name_inside_a_string_literal_is_left_alone()
    {
        var rewriter = ParameterRewriter.Parse("SELECT '@notaparam' AS s FROM bars WHERE a = @real");

        Assert.Equal(["real"], rewriter.Parameters.Select(p => p.Name));
        Assert.Equal("SELECT '@notaparam' AS s FROM bars WHERE a = ?", Render(rewriter));
    }

    [Fact]
    public void A_doubled_quote_inside_a_string_literal_does_not_end_it()
    {
        var rewriter = ParameterRewriter.Parse("SELECT 'it''s @notaparam' FROM bars WHERE a = @real");

        Assert.Single(rewriter.Parameters);
        Assert.Equal("real", rewriter.Parameters[0].Name);
    }

    [Fact]
    public void A_quoted_identifier_is_left_alone()
    {
        var rewriter = ParameterRewriter.Parse("SELECT \"@col\" FROM bars WHERE a = @real");

        Assert.Single(rewriter.Parameters);
        Assert.Equal("SELECT \"@col\" FROM bars WHERE a = ?", Render(rewriter));
    }

    [Fact]
    public void A_line_comment_containing_a_question_mark_is_ignored()
    {
        var rewriter = ParameterRewriter.Parse("SELECT a -- what about ? and @name\nFROM bars WHERE b = ?");

        Assert.Single(rewriter.Parameters);
        Assert.Equal(ParameterStyle.Positional, rewriter.Style);
    }

    [Fact]
    public void A_block_comment_is_ignored()
    {
        var rewriter = ParameterRewriter.Parse("SELECT a /* ? @name $1 */ FROM bars WHERE b = @only");

        Assert.Single(rewriter.Parameters);
        Assert.Equal("only", rewriter.Parameters[0].Name);
    }

    [Fact]
    public void A_dollar_not_followed_by_digits_is_not_a_parameter()
    {
        var rewriter = ParameterRewriter.Parse("SELECT 'a$b' FROM bars WHERE c = @x");

        Assert.Single(rewriter.Parameters);
        Assert.Equal("x", rewriter.Parameters[0].Name);
    }

    // ---- list parameters (D29) ----

    [Fact]
    public void A_parameter_in_an_in_position_accepts_a_list()
    {
        var rewriter = ParameterRewriter.Parse(
            "SELECT * FROM bars WHERE symbol IN @symbols AND ts < @before");

        Assert.True(rewriter.Parameters[0].AcceptsList);
        Assert.False(rewriter.Parameters[1].AcceptsList);
    }

    [Fact]
    public void A_parameter_that_appears_outside_an_in_position_does_not_accept_a_list()
    {
        var rewriter = ParameterRewriter.Parse(
            "SELECT * FROM bars WHERE symbol IN @s OR base = @s");

        Assert.False(rewriter.Parameters[0].AcceptsList);
    }

    [Theory]
    [InlineData(1, "SELECT * FROM bars WHERE symbol IN (?)")]
    [InlineData(2, "SELECT * FROM bars WHERE symbol IN (?, ?)")]
    [InlineData(3, "SELECT * FROM bars WHERE symbol IN (?, ?, ?)")]
    public void A_list_expands_to_that_many_placeholders(int length, string expected)
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol IN @symbols");

        var rendered = rewriter.Render([length]);

        Assert.Equal(expected, rendered.Sql);
        Assert.Equal(length, rendered.Slots.Count);
    }

    /// <summary>
    /// <c>x IN (NULL)</c> is NULL for every x, so <c>IS FALSE</c> yields FALSE — the SQL result of an
    /// empty <c>IN</c> list — without ever touching the left operand.
    /// </summary>
    [Fact]
    public void An_empty_in_list_becomes_a_test_that_is_always_false()
    {
        var rewriter = ParameterRewriter.Parse("SELECT COUNT(*) FROM bars WHERE symbol IN @symbols");

        var rendered = rewriter.Render([0]);

        Assert.Equal("SELECT COUNT(*) FROM bars WHERE symbol IN (?) IS FALSE", rendered.Sql);
        Assert.Single(rendered.Slots);
    }

    /// <summary>And <c>NOT IN</c> of an empty list is TRUE, by the same three-valued reasoning.</summary>
    [Fact]
    public void An_empty_not_in_list_becomes_a_test_that_is_always_true()
    {
        var rewriter = ParameterRewriter.Parse("SELECT COUNT(*) FROM bars WHERE symbol NOT IN @symbols");

        var rendered = rewriter.Render([0]);

        Assert.Equal("SELECT COUNT(*) FROM bars WHERE symbol NOT IN (?) IS NOT FALSE", rendered.Sql);
    }

    [Fact]
    public void A_list_works_in_every_style()
    {
        Assert.Equal(
            "SELECT * FROM bars WHERE symbol IN (?, ?)",
            ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol IN ?").Render([2]).Sql);
        Assert.Equal(
            "SELECT * FROM bars WHERE symbol IN (?, ?)",
            ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol IN $1").Render([2]).Sql);
        Assert.Equal(
            "SELECT * FROM bars WHERE symbol IN (?, ?)",
            ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol IN @s").Render([2]).Sql);
    }

    // ---- binder classification ----

    [Theory]
    [InlineData("BTCUSDT", false)]
    [InlineData(42, false)]
    [InlineData(null, false)]
    public void A_scalar_is_not_a_list(object? value, bool expected) =>
        Assert.Equal(expected, ParameterBinder.IsList(value));

    [Fact]
    public void A_string_bound_to_an_in_parameter_is_a_scalar_not_seven_characters()
    {
        Assert.False(ParameterBinder.IsList("BTCUSDT"));
        Assert.False(ParameterBinder.IsList(new byte[] { 1, 2, 3 }));
        Assert.True(ParameterBinder.IsList(new[] { "BTCUSDT", "ETHUSDT" }));
        Assert.True(ParameterBinder.IsList(new List<int> { 1, 2 }));
    }

    [Fact]
    public void Binding_a_list_to_a_non_in_parameter_names_the_parameter()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol = @symbols");

        var error = Assert.Throws<ArgumentException>(
            () => ParameterBinder.ShapeOf(rewriter.Parameters, [new[] { "a", "b" }]));

        Assert.Contains("@symbols", error.Message, StringComparison.Ordinal);
        Assert.Contains("not in an IN position", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_name_at_bind_time_names_it()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol = @symbol");

        var error = Assert.Throws<ArgumentException>(
            () => ParameterBinder.ResolveNamed(
                rewriter.Parameters,
                new Dictionary<string, object?> { ["nope"] = 1 }));

        Assert.Contains("@symbol", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_extra_bound_value_is_reported_rather_than_ignored()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE symbol = @symbol");

        var error = Assert.Throws<ArgumentException>(
            () => ParameterBinder.ResolveNamed(
                rewriter.Parameters,
                new Dictionary<string, object?> { ["symbol"] = "BTC", ["extra"] = 1 }));

        Assert.Contains("@extra", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_anonymous_object_binds_by_property_name()
    {
        var rewriter = ParameterRewriter.Parse(
            "SELECT * FROM bars WHERE symbol = @symbol AND ts >= @since");

        var values = ParameterBinder.ResolveNamed(
            rewriter.Parameters,
            new { symbol = "BTCUSDT", since = new DateTime(2026, 1, 3) });

        Assert.Equal("BTCUSDT", values[0]);
        Assert.Equal(new DateTime(2026, 1, 3), values[1]);
    }

    [Fact]
    public void A_wrong_positional_count_is_reported_before_execution()
    {
        var rewriter = ParameterRewriter.Parse("SELECT * FROM bars WHERE a = ? AND b = ?");

        var error = Assert.Throws<ArgumentException>(
            () => ParameterBinder.ResolvePositional(rewriter.Parameters, ["only one"], ParameterStyle.Positional));

        Assert.Contains("2 '?' placeholders", error.Message, StringComparison.Ordinal);
    }

    // ---- the corpus is the source of truth for the five rewritten queries ----

    [Theory]
    [InlineData("31_params_named")]
    [InlineData("32_params_ordinal_reuse")]
    [InlineData("33_params_list_in")]
    [InlineData("34_params_list_empty_in")]
    [InlineData("35_params_list_empty_not_in")]
    public void The_rewriter_reproduces_the_checked_in_planner_sql(string name)
    {
        var query = CorpusQueries.Load().Single(q => q.Name == name);

        Assert.True(query.IsRewritten, $"{name} should have a twin in corpus/queries/m1-rewritten");
        Assert.Equal(Normalise(query.PlannerSql), Normalise(Render(query.Sql)));
    }

    private static string Render(ParameterRewriter rewriter) =>
        rewriter.Render(rewriter.PrepareShape()).Sql;

    private static string Normalise(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
