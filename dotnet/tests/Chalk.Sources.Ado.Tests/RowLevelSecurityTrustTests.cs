using Chalk.Catalog;
using Chalk.Entitlements;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// Trusting a source's row-level security is a claim Chalk cannot check, so the builder takes the
/// claim's three pre-conditions by name and refuses fewer (D156, D310).
/// </summary>
public sealed class RowLevelSecurityTrustTests
{
    private const RowLevelSecurityPreconditions Every =
        RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal
        | RowLevelSecurityPreconditions.PoliciesEnabledAndForced
        | RowLevelSecurityPreconditions.PoliciesMatchEntitlements;

    [Fact]
    public void A_source_is_enforced_until_the_host_says_otherwise()
    {
        var schema = Builder().Build().DescribeSchema();

        Assert.False(schema.TrustSourceRowLevelSecurity);
    }

    [Fact]
    public void Every_precondition_asserted_by_name_is_the_claim()
    {
        var schema = Builder().TrustSourceRowLevelSecurity(Every).Build().DescribeSchema();

        Assert.True(schema.TrustSourceRowLevelSecurity);
    }

    [Theory]
    [InlineData(RowLevelSecurityPreconditions.None, "ConnectionIdentifiesPrincipal, PoliciesEnabledAndForced, PoliciesMatchEntitlements")]
    [InlineData(RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal, "PoliciesEnabledAndForced, PoliciesMatchEntitlements")]
    [InlineData(
        RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal | RowLevelSecurityPreconditions.PoliciesMatchEntitlements,
        "PoliciesEnabledAndForced")]
    public void A_claim_short_of_a_precondition_is_refused_naming_what_it_left_out(
        RowLevelSecurityPreconditions asserted, string missing)
    {
        var refused = Assert.Throws<ArgumentException>(() => Builder().TrustSourceRowLevelSecurity(asserted));

        Assert.Equal("asserted", refused.ParamName);
        Assert.Contains($"does not assert {missing}", refused.Message, StringComparison.Ordinal);
        Assert.Contains("every row the source returns is disclosed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_names_exactly_what_was_not_asserted()
    {
        Assert.Equal(RowLevelSecurityPreconditions.None, Every.Missing());
        Assert.Equal(
            RowLevelSecurityPreconditions.PoliciesEnabledAndForced | RowLevelSecurityPreconditions.PoliciesMatchEntitlements,
            RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal.Missing());
    }

    private static AdoSourceBuilder Builder()
    {
        var provider = new FakeProvider { Rows = [] };
        return new AdoSourceBuilder("fake", provider.Connect, "main")
            .Dialect(DialectProfiles.Ansi)
            .AddTable("t", [new() { Name = "id", Type = ChalkType.Int64() }]);
    }
}
