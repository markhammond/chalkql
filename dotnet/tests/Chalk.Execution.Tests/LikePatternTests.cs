using Chalk.Sources;

namespace Chalk.Execution.Tests;

/// <summary>
/// D312, D313 — the escape rules a parameter's value is held to when it is bound, which are the ones
/// the planner holds a literal to (Calcite's runtime's, and the SQL standard's), and what a pattern's
/// literal start is under them. The planner's <c>LikePatternsTest</c> states the same table.
/// </summary>
public sealed class LikePatternTests
{
    [Fact]
    public void No_escape_clause_makes_every_pattern_well_formed()
    {
        Assert.Null(LikePattern.EscapeDefect(null));
        Assert.Null(LikePattern.PatternDefect(@"KB\_%", null));
        Assert.Null(LikePattern.PatternDefect("ab!", null));
    }

    [Fact]
    public void An_escape_is_exactly_one_character()
    {
        Assert.Null(LikePattern.EscapeDefect("!"));
        Assert.Null(LikePattern.EscapeDefect("😀"));
        Assert.Contains("not the empty string", LikePattern.EscapeDefect(string.Empty), StringComparison.Ordinal);
        Assert.Contains("not 2 characters", LikePattern.EscapeDefect("!!"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KB!_%", null)]
    [InlineData("100!%", null)]
    [InlineData("a!!b", null)]
    [InlineData("ab!", "ends with its escape character '!' at position 3")]
    [InlineData("a!b", "before an ordinary character at position 2")]
    [InlineData("😀!x", "before an ordinary character at position 2")]
    public void An_escape_must_be_followed_by_a_wildcard_or_itself(string pattern, string? defect)
    {
        var found = LikePattern.PatternDefect(pattern, "!");
        if (defect is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(defect, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_refusal_never_quotes_the_pattern()
    {
        var error = Assert.Throws<ArgumentException>(() => LikePattern.Validate("secret!x", "!"));

        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KB%", null, "KB", true)]
    [InlineData(@"KB\_%", null, @"KB\", false)]
    [InlineData(@"KB\_%", @"\", "KB_", true)]
    [InlineData("KB!%%", "!", "KB%", true)]
    [InlineData("KB!!%", "!", "KB!", true)]
    [InlineData("100!%", "!", "100%", false)]
    [InlineData("%BLK", null, "", false)]
    [InlineData("%", null, "", true)]
    [InlineData("KB-%-BLK", null, "KB-", false)]
    [InlineData("KB%%", null, "KB", false)]
    [InlineData("", null, "", false)]
    [InlineData("😀%", null, "😀", true)]
    public void The_literal_start_reads_through_the_escape(
        string pattern, string? escape, string start, bool bare)
    {
        Assert.Equal(start, LikePattern.LiteralStart(pattern, escape, out var barePrefix));
        Assert.Equal(bare, barePrefix);
    }

    /// <summary>The bare-prefix rule <see cref="IndexPrefix"/> always stated is the no-escape case.</summary>
    [Theory]
    [InlineData("BTC%")]
    [InlineData("%")]
    [InlineData("a_b%")]
    [InlineData("a%b%")]
    [InlineData("%USDT")]
    [InlineData("BTCUSDT")]
    [InlineData("")]
    public void The_bare_prefix_rule_agrees_with_index_prefix(string pattern)
    {
        LikePattern.LiteralStart(pattern, null, out var bare);

        Assert.Equal(IndexPrefix.IsBarePrefix(pattern), bare);
    }
}
