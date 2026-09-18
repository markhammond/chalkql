using System.Collections.Concurrent;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The half of the adversarial battery that is not a statement (D251 class 5,
/// <c>docs/design/32-adversarial-entitlements.md</c> §1): narrowing a plan towards a tenant it was
/// not bound to, the options a host may set, the channels beside the rows, and what the tree does
/// with a statement that spells <c>@ctx</c> itself.
/// </summary>
/// <remarks>
/// <para>
/// The attacker here controls the statement, its parameters and — where a host would let them —
/// nothing else; the options are the host's. So what these cases ask is whether an option a host
/// might reasonably set changes what a principal receives. The answer has to be no for every one of
/// them, and D252's detector is over every channel in each.
/// </para>
/// <para>
/// The pushdown sweep over a <em>database</em> is <see cref="TenancyRemoteBindingTests"/>'s, where
/// the level decides what a source is actually sent; this class sweeps the same levels in process,
/// where they decide what the local gate does, and adds the capability sets beside them.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementSubversionTests(SharedSidecar sidecar)
{
    /// <summary>
    /// The statements the option sweep runs: a probe on a masked column, a grouping by one, a
    /// self-join across two leaves, a count through an invisible parent, and a union of two
    /// occurrences of one table — the shapes where a pushdown decision could move a disclosure.
    /// </summary>
    private static readonly string[] Swept =
    [
        "01_probe_equality_against_a_literal",
        "11_probe_group_by_the_masked_column",
        "20_self_join_on_the_masked_column",
        "28_lateral_count_over_an_invisible_child",
        "33_union_of_two_aliases_of_one_table",
    ];

    private static readonly PushdownLevel[] Levels =
    [
        PushdownLevel.Full,
        PushdownLevel.FiltersOnly,
        PushdownLevel.ProjectionOnly,
        PushdownLevel.None,
    ];

    /// <summary>
    /// The capabilities that decide where an entitled leaf's own work happens: whether its filter
    /// travels, whether its projection does, whether the tenancy list may be an IN list, and
    /// whether an aggregate, a join or a DISTINCT above it stays here.
    /// </summary>
    private static readonly DisabledCapability[][] Capabilities =
    [
        [DisabledCapability.Filter],
        [DisabledCapability.Project],
        [DisabledCapability.InList],
        [DisabledCapability.Aggregate],
        [DisabledCapability.Join],
        [DisabledCapability.Distinct],
        [DisabledCapability.Filter, DisabledCapability.Project, DisabledCapability.InList],
    ];

    // ---------------------------------------------------------------- narrowing

    /// <summary>
    /// D232 and D233: a narrowing may only narrow. A plan whose tenancy is folded answers for that
    /// tenancy whoever runs it, so binding another one is refused by name rather than quietly
    /// re-planned — which is the difference between a shared plan and a plan somebody else's.
    /// </summary>
    [Fact]
    public async Task A_plan_bound_to_one_tenant_cannot_be_narrowed_towards_another()
    {
        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";

        // The tenant's own plan: the grants folded, the subject left open (§2.1).
        var tenant = await entitled.PrepareAsync(
            Sql, TenancyFixture.PartiallyBound(TenancyFixture.U1));

        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            async () => await tenant.NarrowAsync(OtherTenant));
        Assert.Contains("manager_orgs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be given another value", refusal.Message, StringComparison.Ordinal);

        // And the refusal says nothing about either tenancy's rows.
        LeakDetector.For("u1", TenancyFixture.U1).Inspect(new LeakScan
        {
            Statement = "a narrowing towards another tenant",
            Refusal = refusal.Message,
        });
    }

    /// <summary>
    /// The same from the other end: a plan with nothing folded narrowed to one tenant, and then
    /// towards a second. The first narrowing is what D232 is for; the second is refused.
    /// </summary>
    [Fact]
    public async Task A_shared_plan_narrowed_once_cannot_be_narrowed_towards_a_second_tenant()
    {
        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";

        var shared = await entitled.PrepareAsync(Sql, TenancyFixture.U1.Shape());
        var narrowed = await shared.NarrowAsync(OneTenant);

        // The narrowed plan is the tenant's, and the rows are that tenant's rows.
        var rows = await RowsAsync(engine, narrowed, TenancyFixture.U1);
        Assert.Equal(["1", "2", "3", "4", "7"], [.. rows.Select(r => r.Split('|')[0])]);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            async () => await narrowed.NarrowAsync(OtherTenant));
        Assert.Contains("manager_orgs", refusal.Message, StringComparison.Ordinal);
    }

    private static RequestContext OneTenant => Part("manager_orgs", 1);

    private static RequestContext OtherTenant => Part("manager_orgs", 2);

    private static RequestContext Part(string name, int org) => new()
    {
        Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            [name] = new ContextRelation
            {
                Columns = ["id"],
                Rows = [[org]],
                ColumnTypes = [ChalkType.Int32()],
            },
        },
    };

    // ---------------------------------------------------------------- the options

    /// <summary>
    /// Every pushdown level and every entitlements-relevant capability set, over the statements
    /// where a pushdown decision could move a disclosure: the rows are the ones the default gave,
    /// and the detector is clean in each.
    /// </summary>
    [Theory]
    [MemberData(nameof(SweptStatements))]
    public async Task No_option_a_host_may_set_changes_what_a_principal_receives(string name)
    {
        var query = CorpusQueries.LoadM7Adversarial().Single(q => q.Name == name);
        await using var engine = await EngineAsync();

        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            var baseline = await RowsUnderAsync(engine, query, context, query.PrepareOptions(), detector, principal);

            foreach (var level in Levels)
            {
                Assert.Equal(
                    baseline,
                    await RowsUnderAsync(
                        engine, query, context, query.PrepareOptions(level), detector, principal));
            }

            foreach (var disabled in Capabilities)
            {
                Assert.Equal(
                    baseline,
                    await RowsUnderAsync(
                        engine, query, context, query.PrepareOptions(disabled), detector, principal));
            }
        }
    }

    public static TheoryData<string> SweptStatements()
    {
        var data = new TheoryData<string>();
        foreach (var name in Swept)
        {
            data.Add(name);
        }

        return data;
    }

    // ---------------------------------------------------------------- the other channels

    /// <summary>
    /// D252 over the two channels a row comparison never reads: the plan text a host may log, and
    /// the text of every refusal. Every statement of both families, as every principal.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatement))]
    public async Task The_plan_text_and_every_refusal_are_clean(string family, string name)
    {
        var query = (family == "m7-tenancy"
            ? CorpusQueries.LoadM7Tenancy()
            : CorpusQueries.LoadM7Adversarial()).Single(q => q.Name == name);
        var options = new PrepareOptions
        {
            Pushdown = PushdownLevel.Full,
            Conformance = query.Conformance,
            Libraries = query.Libraries,
            IncludePlanText = true,
        };

        await using var engine = await EngineAsync();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            try
            {
                var prepared = await engine.WithEntitlements().PrepareAsync(query.Sql, context, options);
                Assert.NotNull(prepared.Query.PlanText);
                detector.Inspect(new LeakScan
                {
                    Statement = $"{name} (plan text)",
                    PlanText = prepared.Query.PlanText,
                    Report = string.Join(
                        ", ",
                        prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                            .Concat(prepared.Entitlements.Tables.Select(
                                t => $"{t.Table}:{t.Visibility}"))),
                });
            }
            catch (Exception failed) when (TenancyCorpusTests.IsRegistered(failed))
            {
                // A refusal is a channel like any other: what it says is read, never what it is.
                detector.Inspect(new LeakScan
                {
                    Statement = $"{name} (refusal text)",
                    Refusal = failed.Message,
                });
            }
        }
    }

    public static TheoryData<string, string> EveryStatement()
    {
        var data = new TheoryData<string, string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            data.Add("m7-tenancy", query.Name);
        }

        foreach (var query in CorpusQueries.LoadM7Adversarial())
        {
            if (!TenancyCorpusTests.NotPlanned.ContainsKey(query.Name))
            {
                data.Add("m7-adversarial", query.Name);
            }
        }

        return data;
    }

    // ---------------------------------------------------------------- the client body

    /// <summary>
    /// §7: a client body runs in the host's own process, which is the one place a raw value would
    /// be invisible to the plan. It is handed the column's <em>disclosed</em> form, so the function
    /// can say what it was given and what it was given is the mask.
    /// </summary>
    [Fact]
    public async Task A_client_body_is_handed_the_masked_value_and_says_so()
    {
        var seen = new ConcurrentBag<string>();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Subversion.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = registry =>
            {
                registry.AddScalar<int, bool>("is_vip", static id => id % 2 == 1);
                registry.AddScalar<string, string>("echo", s =>
                {
                    seen.Add(s);
                    return s;
                });
            },
        });

        // u2 is an agent in organisation 1: every name they can see is initial-masked.
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync("SELECT id, echo(first_name) AS seen FROM members ORDER BY id", TenancyFixture.U2);
        var rows = await RowsAsync(engine, prepared, TenancyFixture.U2);

        Assert.Equal(["1|T", "2|B"], rows);
        Assert.Equal(["B", "T"], [.. seen.Order(StringComparer.Ordinal)]);
        LeakDetector.For("u2", TenancyFixture.U2).Inspect(new LeakScan
        {
            Statement = "a client body over a masked column",
            Rows = [.. seen],
            Columns = ["what the host was handed"],
        });
    }

    // ---------------------------------------------------------------- @ctx in a statement

    /// <summary>
    /// What the tree does with a statement that spells the context vocabulary itself. The answer,
    /// recorded here and in design 32's as-built section: it is <b>never</b> a context reference.
    /// The client's <c>@name</c> rewriter (D27) turns <c>@ctx</c> into an ordinary named parameter
    /// and leaves the <c>.name</c> after it, so the statement is refused as an
    /// <c>INVALID_REQUEST</c> — at the parser where a scalar was written, and at the validator
    /// inside an <c>IN</c> list, where the parameter is legal text in an illegal position.
    /// </summary>
    /// <remarks>
    /// The third spelling is the one D223 reserves: a quoted identifier beginning with
    /// <c>$chalk$</c> is refused before any rewrite reads it, so the per-request context schema
    /// cannot be named either. None of the four reaches a bound value, and no message quotes one.
    /// The kinds differ and are asserted as they are: <c>PARSE</c> where a scalar was written,
    /// <c>VALIDATION</c> inside an <c>IN</c> list, and <c>INVALID_REQUEST</c> for the reserved
    /// marker, which is refused before anything is parsed.
    /// </remarks>
    [Theory]
    [InlineData(
        "SELECT id FROM members WHERE id = @ctx.user ORDER BY id",
        "Encountered \". user\"",
        Chalk.Client.Rpc.PlanErrorKind.Parse)]
    [InlineData(
        "SELECT @ctx.user AS u FROM members",
        "Encountered \". user\"",
        Chalk.Client.Rpc.PlanErrorKind.Parse)]
    [InlineData(
        "SELECT id FROM members WHERE org_id IN (@ctx.manager_orgs) ORDER BY id",
        "Illegal use of dynamic parameter",
        Chalk.Client.Rpc.PlanErrorKind.Validation)]
    [InlineData(
        "SELECT id FROM members WHERE org_id IN (\"$chalk$ctx\".\"manager_orgs\") ORDER BY id",
        "begins with $chalk$",
        Chalk.Client.Rpc.PlanErrorKind.InvalidRequest)]
    public async Task A_statement_that_spells_the_context_itself_is_refused(
        string sql, string says, Chalk.Client.Rpc.PlanErrorKind kind)
    {
        await using var engine = await EngineAsync();
        var refusal = await Assert.ThrowsAsync<PlanningException>(
            () => engine.WithEntitlements().PrepareAsync(sql, TenancyFixture.U1).AsTask());

        Assert.Equal(kind, refusal.Kind);
        Assert.Contains(says, refusal.Message, StringComparison.Ordinal);
        LeakDetector.For("u1", TenancyFixture.U1).Inspect(new LeakScan
        {
            Statement = "a statement spelling @ctx",
            Refusal = refusal.Message,
        });
    }

    // ---------------------------------------------------------------- helpers

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Subversion.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private static async Task<string[]> RowsUnderAsync(
        ChalkEngine engine,
        CorpusQuery query,
        RequestContext context,
        PrepareOptions options,
        LeakDetector detector,
        string principal)
    {
        var prepared = await engine.WithEntitlements().PrepareAsync(query.Sql, context, options);
        var rows = await RowsAsync(engine, prepared, context);
        detector.Inspect(new LeakScan
        {
            Statement = $"{query.Name} under {options.Pushdown} for {principal}",
            Rows = rows,
            Columns = [.. prepared.Columns.Select(c => c.Name)],
            Report = string.Join(
                ", ",
                prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                    .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}"))),
        });
        return rows;
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, EntitledQuery prepared, RequestContext context)
    {
        var rows = new List<string>();
        // A plan that folded everything carries its own context and refuses a second one; a plan
        // with an open half needs the values at execution (§2, §2.1). The plan itself says which.
        await using var execution = prepared.Query.RequiredContext.Count == 0
            ? await engine.ExecuteAsync(prepared.Query, (IReadOnlyList<object?>?)null)
            : await engine.ExecuteAsync(prepared.Query, context);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return [.. rows];
    }
}
