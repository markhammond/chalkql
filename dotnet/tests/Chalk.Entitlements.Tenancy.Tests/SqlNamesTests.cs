namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// The names the compiler writes into a descriptor's SQL: as they are where the parser reads them as
/// that name, and quoted where it would not, so a host never renames a column to suit SQL.
/// </summary>
public sealed class SqlNamesTests
{
    /// <summary>A plain name SQL does not reserve is written as it is, so descriptors keep their text.</summary>
    [Theory]
    [InlineData("MissionId")]
    [InlineData("org_id")]
    [InlineData("_hidden")]
    [InlineData("Opened")]
    public void A_plain_unreserved_name_is_written_as_it_is(string name) =>
        Assert.Equal(name, SqlNames.Of(name));

    /// <summary>
    /// A reserved word is quoted in any case: <c>Open</c>, <c>At</c> and <c>Value</c> would not parse,
    /// and <c>user</c> would parse as SQL's <c>USER</c>, never the column.
    /// </summary>
    [Theory]
    [InlineData("Open", "\"Open\"")]
    [InlineData("at", "\"at\"")]
    [InlineData("VALUE", "\"VALUE\"")]
    [InlineData("User", "\"User\"")]
    [InlineData("current_date", "\"current_date\"")]
    public void A_reserved_word_is_quoted(string name, string written) =>
        Assert.Equal(written, SqlNames.Of(name));

    /// <summary>A name that is not a plain identifier is quoted, its own quotes doubled.</summary>
    [Theory]
    [InlineData("order id", "\"order id\"")]
    [InlineData("1st", "\"1st\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void A_name_that_is_not_a_plain_identifier_is_quoted(string name, string written) =>
        Assert.Equal(written, SqlNames.Of(name));

    /// <summary>The embedded list holds the words, and none of its comment.</summary>
    [Fact]
    public void The_list_is_loaded()
    {
        Assert.True(SqlNames.IsReserved("select"));
        Assert.True(SqlNames.IsReserved("national"));
        Assert.False(SqlNames.IsReserved("MissionId"));
        Assert.False(SqlNames.IsReserved("#"));
    }
}
