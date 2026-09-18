using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The three-valued visibility half of <c>Reconcile</c>
/// (<c>docs/design/29-entitlements-as-a-wrapper.md</c> §4, D215).
/// </summary>
/// <remarks>
/// The per-column half is proved by the corpus, which runs every query as every principal through
/// both layers. What is under test here is the *table* verdict: the package predicts how much of a
/// table a principal can see from the grants alone and compares it with what the planner's fold
/// reported — and says <c>Indeterminate</c>, rather than guessing, for a host predicate outside the
/// grammar it recognises. A guess that happened to agree would be worse than no answer.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyReconcileTests(SharedSidecar sidecar)
{
    private const string Statement = "SELECT id, org_id FROM invites ORDER BY id";

    /// <summary>The structured form: the package knows the column and the list, so it can say.</summary>
    [Fact]
    public async Task The_structured_predicate_reconciles()
    {
        var (engine, entitlements) = await EngineAsync(
            t => t.Tenancy(r => r.In(t.Column("org_id"), "allowed_orgs")));

        await using (engine)
        {
            // A principal who holds the list sees some rows; one who holds none sees no row, and
            // the package predicts both.
            foreach (var (orgs, expected) in new[]
            {
                (new[] { 1 }, TableVisibility.Some),
                ([], TableVisibility.None),
            })
            {
                var principal = TenancyPolicyFixture.HostList(7, orgs);
                var prepared = await engine
                    .WithEntitlements()
                    .PrepareAsync(Statement, entitlements.Bind(principal));

                var reconciled = entitlements.Reconcile(prepared, principal);
                var invites = Assert.Single(reconciled.Visibility);
                Assert.Equal(ReconciledVisibility.Agrees, invites.Verdict);
                Assert.Equal(expected, invites.Predicted);
                Assert.Equal(expected, invites.Reported);
                Assert.True(reconciled.Agrees);
            }
        }
    }

    /// <summary>Free text in the recognised grammar reconciles exactly as the structured form does.</summary>
    [Fact]
    public async Task Free_text_inside_the_grammar_reconciles()
    {
        var (engine, entitlements) = await EngineAsync(
            t => t.Tenancy(r => r.Predicate(Sql.Of("org_id IN (@ctx.allowed_orgs)"))));

        await using (engine)
        {
            var principal = TenancyPolicyFixture.HostList(7, 1);
            var prepared = await engine
                .WithEntitlements()
                .PrepareAsync(Statement, entitlements.Bind(principal));

            var invites = Assert.Single(entitlements.Reconcile(prepared, principal).Visibility);
            Assert.Equal(ReconciledVisibility.Agrees, invites.Verdict);
            Assert.Equal(TableVisibility.Some, invites.Predicted);
        }
    }

    /// <summary>
    /// Free text outside it does not: the package says <c>Indeterminate</c> and reports what the
    /// plan said beside it, and the reconciliation still <em>agrees</em>, because nothing disagreed.
    /// </summary>
    [Fact]
    public async Task Free_text_outside_the_grammar_is_indeterminate_and_is_never_counted()
    {
        var (engine, entitlements) = await EngineAsync(
            t => t.Tenancy(r => r.Predicate(
                Sql.Of("id > 0 AND (org_id IN (@ctx.allowed_orgs) OR id < 100)"))));

        await using (engine)
        {
            var principal = TenancyPolicyFixture.HostList(7, 1);
            var prepared = await engine
                .WithEntitlements()
                .PrepareAsync(Statement, entitlements.Bind(principal));

            var reconciled = entitlements.Reconcile(prepared, principal);
            var invites = Assert.Single(reconciled.Visibility);
            Assert.Equal(ReconciledVisibility.Indeterminate, invites.Verdict);
            Assert.Null(invites.Predicted);
            Assert.Equal(TableVisibility.Some, invites.Reported);

            // Indeterminate is not a disagreement, and it is not an agreement either: what it is is
            // reported.
            Assert.True(reconciled.Agrees);
            Assert.Empty(reconciled.Differences);
        }
    }

    // ---------------------------------------------------------------- the fixture

    /// <summary>The tenancy fixture's schema, with <c>invites</c> restricted as the test says.</summary>
    /// <remarks>
    /// The policy is declared over a catalog and the table comes from the source it belongs to, so
    /// the test writes its restriction against the handle rather than against a name (D270).
    /// </remarks>
    private async Task<(ChalkEngine Engine, TenancyEntitlements Entitlements)> EngineAsync(
        Action<Table> invites)
    {
        var schema = Tables(null).DescribeSchema();
        var policy = TenancyPolicy.Declare(new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [schema],
        });
        policy.Role("manager");
        invites(policy.Source(TenancyPolicyFixture.SourceName).Table("invites"));
        var entitlements = policy.Compile([schema]);
        var source = Tables(entitlements);
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });
        return (engine, entitlements);
    }

    private static PocoSource Tables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("invites", TenancyFixture.Invites, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            if (entitlements?.For(TenancyPolicyFixture.SourceName, "invites") is { } descriptor)
            {
                t.Entitlement(descriptor);
            }
        });
        return builder.Build();
    }
}
