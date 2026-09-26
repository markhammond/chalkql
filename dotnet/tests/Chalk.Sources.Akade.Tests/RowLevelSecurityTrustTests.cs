using Akade.IndexedSet;
using Chalk.Entitlements;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// The Akade builders take the same three pre-conditions by name as the ADO builder does, and refuse
/// fewer (D156, D310).
/// </summary>
public sealed class RowLevelSecurityTrustTests
{
    private sealed record Named(int Id, string Name);

    [Fact]
    public void Every_precondition_asserted_by_name_is_the_claim()
    {
        var source = Names()
            .TrustSourceRowLevelSecurity(
                RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal
                | RowLevelSecurityPreconditions.PoliciesEnabledAndForced
                | RowLevelSecurityPreconditions.PoliciesMatchEntitlements)
            .Build();

        Assert.True(source.DescribeSchema().TrustSourceRowLevelSecurity);
        Assert.False(Names().Build().DescribeSchema().TrustSourceRowLevelSecurity);
    }

    [Fact]
    public void A_claim_short_of_a_precondition_is_refused_naming_what_it_left_out()
    {
        var refused = Assert.Throws<ArgumentException>(() =>
            Names().TrustSourceRowLevelSecurity(RowLevelSecurityPreconditions.PoliciesEnabledAndForced));

        Assert.Equal("asserted", refused.ParamName);
        Assert.Contains("does not assert ConnectionIdentifiesPrincipal, PoliciesMatchEntitlements", refused.Message, StringComparison.Ordinal);
    }

    private static IndexedSetSourceBuilder<Named> Names() =>
        AkadeSource.From("names", new[] { new Named(1, "b"), new Named(2, "a") }.ToIndexedSet().Build());
}
