using System.Text;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// Design 38 §8's two-source layout <b>declared with D270's handles</b> (F84): the same policy body
/// as the co-located fixture, with the marketplace's two tables obtained from a second source and the
/// one line that follows from it — <c>order_items.item_id References items.id</c>, the association a
/// step across two sources resolves through (<c>docs/design/45-typed-tenancy-surface.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TenancySplitCorpusTests"/> runs the family over the layout with the descriptors written
/// out by hand, which is what lets it share the co-located family's oracle, detector and principals
/// and so be a differential. This is the other half: that the <b>typed surface</b> can say it, and
/// that what it compiles to answers the same. One <c>Body</c> and two layouts, so the claim that the
/// policy is the same is a fact about the code rather than about two transcriptions of it.
/// </para>
/// <para>
/// The statements are the corpus's own, with the two tables this layout moved qualified by their
/// schema (A4), and they are held to the very goldens the co-located layer-A run recorded.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancySplitPolicyTests(SharedSidecar sidecar)
{
    private static readonly DirectoryInfo Goldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy"));

    private static readonly Lazy<Compiled> Fixture = new(Build);

    private sealed record Compiled(
        TenancyEntitlements Entitlements,
        IReadOnlyList<AssociationDescriptor> Associations,
        IReadOnlyList<Chalk.Sources.ISourceRuntime> Sources,
        IReadOnlyList<(string Name, TenancyPrincipal Principal)> Principals);

    private static Compiled Build()
    {
        var main = TenancyPolicyFixture.MainTables(null).DescribeSchema();
        var marketplace = TenancyPolicyFixture.MarketplaceTables(null).DescribeSchema();
        var (entitlements, associations) =
            TenancyPolicyFixture.SplitEntitlements(main, marketplace);
        return new Compiled(
            entitlements,
            associations,
            [TenancyPolicyFixture.MainTables(entitlements), TenancyPolicyFixture.MarketplaceTables(entitlements)],
            TenancyPolicyFixture.PrincipalsOf(
                TenancyPolicyFixture.SplitPolicy(new CatalogContext
                {
                    ContextId = TenancyFixture.ContextId,
                    Epoch = TenancyFixture.Epoch,
                    Schemas = [main, marketplace],
                })));
    }

    /// <summary>
    /// The declaration: the association is what the two handles recorded, and the compiled path's
    /// last step and endpoint name the other schema.
    /// </summary>
    [Fact]
    public void The_same_body_declares_the_layout_through_the_association()
    {
        var association = Assert.Single(Fixture.Value.Associations);
        Assert.Equal("main", association.FromSchema);
        Assert.Equal("order_items", association.FromTable);
        Assert.Equal("item_id", association.FromColumn);
        Assert.Equal(TenancyPolicyFixture.MarketplaceSourceName, association.ToSchema);
        Assert.Equal("items", association.ToTable);
        Assert.Equal("id", association.ToColumn);

        var path = Assert.Single(
            Fixture.Value.Entitlements.For("main", "orders")!.Inherited,
            p => p.Kind == "vendor");
        Assert.Equal(TenancyPolicyFixture.MarketplaceSourceName, path.EndpointSchema);
        Assert.Equal("items", path.EndpointTable);
        Assert.Collection(
            path.Steps,
            up => Assert.Equal("order_items", up.Table),
            down =>
            {
                Assert.Equal("items", down.Table);
                Assert.Equal(TenancyPolicyFixture.MarketplaceSourceName, down.Schema);
            });

        // And the catalog carrying it registers, association and all.
        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [.. Fixture.Value.Sources.Select(s => s.DescribeSchema())],
            Associations = Fixture.Value.Associations,
        });
    }

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            if (!TenancyCorpusTests.KnownLeaks.ContainsKey(query.Name)
                && !TenancyCorpusTests.NotPlanned.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// And what the typed declaration compiles to over two sources answers what layer A recorded over
    /// one — statement for statement, principal for principal, row for row and label for label.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Layer_b_over_two_sources_answers_what_layer_a_recorded(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var sql = TenancySplitFixture.Qualified(query.Sql);
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var recorded = new StringBuilder();
        foreach (var (principal, grants) in Fixture.Value.Principals)
        {
            var context = Fixture.Value.Entitlements.Bind(grants);
            recorded.Append("-- ").Append(principal).AppendLine();
            if (refused.Contains(principal))
            {
                var refusal = await Assert.ThrowsAsync<EntitlementException>(
                    async () => await PrepareAsync(sql, context));
                recorded.Append("POLICY ").AppendLine(FirstSentence(refusal.Message));
                recorded.AppendLine();
                continue;
            }

            await using var engine = await EngineAsync();
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
            recorded.Append("report ").AppendLine(string.Join(
                ", ",
                prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                    .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}"))));
            foreach (var row in await RowsAsync(engine, prepared, query))
            {
                recorded.AppendLine(row);
            }

            recorded.AppendLine();

            // And the policy's own prediction beside it, from the grants alone (F87, ADR 0061). The
            // co-located family makes the same comparison; making it here as well is what says the
            // meet along the routes is read off the declarations and not off where the tables
            // happen to live — `order_items` reaches its vendor through an item a source away, and
            // the prediction must be the same one.
            var reconciled = Fixture.Value.Entitlements.Reconcile(prepared, grants);
            Assert.Empty(reconciled.Differences);
            Assert.DoesNotContain(
                reconciled.Visibility, v => v.Verdict == ReconciledVisibility.Disagrees);
            Assert.True(reconciled.Agrees);
        }

        var golden = new FileInfo(Path.Combine(Goldens.FullName, name + ".txt"));
        Assert.True(golden.Exists, $"{golden.FullName} does not exist.");
        Assert.Equal(
            (await File.ReadAllTextAsync(golden.FullName)).ReplaceLineEndings("\n"),
            recorded.ToString().ReplaceLineEndings("\n"));
    }

    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    private async ValueTask<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = Fixture.Value.Sources,
            Associations = Fixture.Value.Associations,
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private async Task PrepareAsync(string sql, RequestContext context)
    {
        await using var engine = await EngineAsync();
        await engine.WithEntitlements().PrepareAsync(sql, context);
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, CorpusQuery query)
    {
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, CorpusQueries.Parameters(query.Name));
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
