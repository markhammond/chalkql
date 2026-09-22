using Chalk.Client;

namespace Chalk.Integration.Tests;

/// <summary>
/// The host's parameter names put back into a redacted text (D287), tested as the pure function it
/// is: the statement form takes bare <c>?</c>s in order and is all or nothing; the plan-text form
/// reads each placeholder's index. Nothing inside a quoted identifier, a string, a comment or a
/// pseudonym's marker is ever touched.
/// </summary>
public sealed class ParameterNamesTests
{
    private static IReadOnlyList<string> Names(string sql)
    {
        var rewriter = ParameterRewriter.Parse(sql);
        return rewriter.SlotNames(rewriter.Render(rewriter.PrepareShape()))!;
    }

    [Fact]
    public void Named_placeholders_are_put_back_in_order()
    {
        var names = Names("SELECT * FROM bars WHERE symbol = @symbol AND ts >= @from AND ts < @to");

        Assert.Equal(["@symbol", "@from", "@to"], names);
        Assert.Equal(
            "SELECT * FROM \"bars\" WHERE \"symbol\" = @symbol AND \"ts\" >= @from AND \"ts\" < @to",
            ParameterNames.Substitute(
                "SELECT * FROM \"bars\" WHERE \"symbol\" = ? AND \"ts\" >= ? AND \"ts\" < ?",
                names,
                indexed: false));
    }

    [Fact]
    public void A_recurring_name_is_put_back_at_every_occurrence()
    {
        var names = Names("SELECT * FROM bars WHERE symbol = @s OR base = @s");

        Assert.Equal(["@s", "@s"], names);
        Assert.Equal(
            "WHERE \"symbol\" = @s OR \"base\" = @s",
            ParameterNames.Substitute("WHERE \"symbol\" = ? OR \"base\" = ?", names, indexed: false));
    }

    [Fact]
    public void A_placeholder_inside_a_quoted_identifier_a_string_or_a_marker_is_left_alone()
    {
        var served =
            "SELECT \"a?b\" FROM t WHERE x = ? AND y = /*REDACTED-0123abcd:CHAR ?*/ AND z = 'q?' -- ?";

        Assert.Equal(
            "SELECT \"a?b\" FROM t WHERE x = @x AND y = /*REDACTED-0123abcd:CHAR ?*/ AND z = 'q?' -- ?",
            ParameterNames.Substitute(served, ["@x"], indexed: false));
    }

    /// <summary>A <c>?</c> is honest and a wrong name is not: unless the count agrees, nothing moves.</summary>
    [Fact]
    public void A_count_that_does_not_agree_leaves_the_text_as_served()
    {
        Assert.Equal(
            "WHERE x = ? AND y = ?",
            ParameterNames.Substitute("WHERE x = ? AND y = ?", ["@x"], indexed: false));
        Assert.Equal(
            "WHERE x = ?",
            ParameterNames.Substitute("WHERE x = ?", ["@x", "@y"], indexed: false));
    }

    [Fact]
    public void The_plan_text_form_reads_each_placeholders_index()
    {
        var text = "Filter condition=[AND(=($0, ?0), >=($1, ?1), <($1, ?9))] query=[WHERE a = ?]";

        Assert.Equal(
            "Filter condition=[AND(=($0, @symbol), >=($1, @from), <($1, ?9))] query=[WHERE a = ?]",
            ParameterNames.Substitute(text, ["@symbol", "@from"], indexed: true));
    }

    [Fact]
    public void Slot_names_exist_only_for_the_named_style()
    {
        foreach (var sql in new[]
        {
            "SELECT * FROM bars WHERE symbol = ?",
            "SELECT * FROM bars WHERE symbol = $1",
            "SELECT * FROM bars",
        })
        {
            var rewriter = ParameterRewriter.Parse(sql);
            Assert.Null(rewriter.SlotNames(rewriter.Render(rewriter.PrepareShape())));
        }
    }
}
