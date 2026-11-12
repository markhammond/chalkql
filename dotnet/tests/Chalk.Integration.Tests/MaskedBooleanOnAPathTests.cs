using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A mask on a <c>BOOLEAN</c> column of a table entitled through a declared path, prepared for a
/// context's shape: the statement runs, and returns what the same statement prepared with the
/// principal's values returns.
/// </summary>
/// <remarks>
/// <para>
/// It was refused as a planner fault, <c>PlanBreaksEntitlements</c>: "output column 'Ready' is
/// reported PerRow and the reads this plan walks say Redacted". Under a shape the column's sanitiser
/// is <c>CASE WHEN v THEN FALSE ELSE NULL END</c>, <c>v</c> the verdict along the path; the planner
/// simplifies a <c>BOOLEAN</c> <c>CASE</c> to <c>AND(NULL, IS NOT TRUE(v))</c> and moves it onto
/// the path's own read of the parent. The client's check took the parent's column the verdict tests
/// as an origin of the value, and that read discloses nothing, so it walked to <c>Redacted</c>
/// against a planner that rightly said <c>PerRow</c>.
/// </para>
/// <para>
/// <c>readiness</c> takes its classification from its asset; a reader holds <c>SECRET</c>, and the
/// reader's rule shows <c>Ready</c> as the mask. A3 is <c>TOP</c>, so its readiness is not shown, and
/// A2 has no readiness at all.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class MaskedBooleanOnAPathTests(SharedSidecar sidecar)
{
    private sealed record Asset(string AssetId, string Classification);

    private sealed record Readiness(string AssetId, bool Ready, int EnduranceMinutes, string Classification = "SECRET");

    private static readonly Asset[] Assets = [new("A1", "SECRET"), new("A2", "SECRET"), new("A3", "TOP")];

    private static readonly Readiness[] Readinesses = [new("A1", true, 90), new("A3", true, 60)];

    private const string LeftJoin =
        "SELECT a.AssetId, r.Ready, r.EnduranceMinutes FROM s.assets a "
        + "LEFT JOIN s.readiness r ON r.AssetId = a.AssetId ORDER BY a.AssetId";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The host's statement: every asset the reader is shown, with its readiness where there is one
    /// it may see, <c>Ready</c> as the mask says. Bound at execution as it is with the values.
    /// </summary>
    [Fact]
    public async Task A_masked_boolean_through_a_path_runs_for_a_shape_as_with_values()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, "Ready", "FALSE");

        string[] expected = ["A1|False|90", "A2|<null>|<null>"];
        Assert.Equal(expected, await fixture.WithValuesAsync(LeftJoin));
        Assert.Equal(expected, await fixture.ForShapeAsync(LeftJoin));
    }

    /// <summary>
    /// With disclosure columns, as the host asked for them: <c>Ready</c> said to be masked where the
    /// mask stands, the same on both paths.
    /// </summary>
    [Fact]
    public async Task Its_disclosure_columns_say_masked_on_both_paths()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, "Ready", "FALSE");
        var options = new EntitlementsOptions { IncludeDisclosureColumns = true };

        string[] expected = ["A1|FULL|False|MASKED|90|FULL", "A2|FULL|<null>|FULL|<null>|FULL"];
        Assert.Equal(expected, await fixture.WithValuesAsync(LeftJoin, options));
        Assert.Equal(expected, await fixture.ForShapeAsync(LeftJoin, options));
    }

    /// <summary>
    /// The table read alone, and a mask that reads the column itself: both run for a shape.
    /// </summary>
    [Theory]
    [InlineData("FALSE", "A1|False")]
    [InlineData("NOT Ready", "A1|False")]
    public async Task The_table_alone_and_a_mask_over_the_column_run_for_a_shape(string mask, string expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, "Ready", mask);
        const string alone = "SELECT AssetId, Ready FROM s.readiness ORDER BY AssetId";

        Assert.Equal([expected], await fixture.WithValuesAsync(alone));
        Assert.Equal([expected], await fixture.ForShapeAsync(alone));
    }

    /// <summary>
    /// The neighbours that ran before: an <c>INTEGER</c> mask through the same path, and the
    /// <c>BOOLEAN</c> mask on a table entitled by a column of its own.
    /// </summary>
    [Fact]
    public async Task An_integer_mask_and_a_table_entitled_by_its_own_column_run_as_before()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using (var integer = await Fixture.CreateAsync(sidecar, "EnduranceMinutes", "0"))
        {
            Assert.Equal(["A1|True|0", "A2|<null>|<null>"], await integer.ForShapeAsync(LeftJoin));
        }

        await using var direct = await Fixture.CreateAsync(sidecar, "Ready", "FALSE", direct: true);
        Assert.Equal(
            ["A1|False", "A3|False"],
            await direct.ForShapeAsync("SELECT AssetId, Ready FROM s.readiness ORDER BY AssetId"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private TenancyEntitlements _entitlements = null!;
        private ChalkEngine _engine = null!;
        private RequestContext _context = null!;

        public static async Task<Fixture> CreateAsync(
            SharedSidecar sidecar, string column, string mask, bool direct = false)
        {
            var fixture = new Fixture();
            var catalog = new CatalogContext { ContextId = "masked-boolean-on-a-path", Schemas = [Source(null).DescribeSchema()] };
            var policy = TenancyPolicy.Declare(catalog);
            var classification = policy.Tenancy("classification");
            var reader = policy.Role("reader");
            var source = policy.Source("s");
            var assets = source.Table("assets");
            var readiness = source.Table("readiness");
            assets.Tenancy(t => t.Direct(classification, assets.Column("Classification")))
                .Visible(new VisibilityRule { Roles = [reader] });
            readiness.Tenancy(t =>
                {
                    if (direct)
                    {
                        t.Direct(classification, readiness.Column("Classification"));
                    }
                    else
                    {
                        t.Inherited(classification).Through(assets);
                    }
                })
                .Visible(new VisibilityRule { Roles = [reader] });
            readiness.Access(new AccessRule
            {
                Roles = [reader], Column = readiness.Column(column), Grants = Verdict.Mask, Mask = Sql.Of(mask),
            });
            fixture._entitlements = policy.Compile(catalog);

            fixture._engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "masked-boolean-on-a-path",
                Sources = [Source(fixture._entitlements)],
                Planner = sidecar.CreatePlanner(),
            }, Ct);
            fixture._context = fixture._entitlements.Bind(new TenancyPrincipal
            {
                User = "u",
                Grants = [Grant.ForTenancy(classification, "SECRET", reader)],
            });
            return fixture;
        }

        public async Task<string[]> WithValuesAsync(string sql, EntitlementsOptions? options = null)
        {
            var prepared = await _engine.WithEntitlements(options ?? new()).PrepareAsync(sql, _context, ct: Ct);
            await using var execution = await _engine.ExecuteAsync(prepared.Query, ct: Ct);
            return await RowsAsync(execution);
        }

        public async Task<string[]> ForShapeAsync(string sql, EntitlementsOptions? options = null)
        {
            var prepared = await _engine.WithEntitlements(options ?? new()).PrepareAsync(sql, _context.Shape(), ct: Ct);
            await using var execution = await _engine.ExecuteAsync(prepared.Query, _context, ct: Ct);
            return await RowsAsync(execution);
        }

        public ValueTask DisposeAsync() => _engine.DisposeAsync();

        private static async Task<string[]> RowsAsync(QueryExecution execution)
        {
            var rows = new List<string>();
            await foreach (var batch in execution.Batches.WithCancellation(Ct))
            {
                using (batch)
                {
                    rows.AddRange(batch.ToRows().Select(r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
                }
            }

            return [.. rows];
        }

        private static PocoSource Source(TenancyEntitlements? entitlements) =>
            new PocoSourceBuilder("s", "s")
                .AddTable("assets", Assets, t =>
                {
                    t.UniqueKey(x => x.AssetId);
                    if (entitlements?.For("s", "assets") is { } d) t.Entitlement(d);
                })
                .AddTable("readiness", Readinesses, t =>
                {
                    t.UniqueKey(x => x.AssetId).ForeignKey<Asset>(r => r.AssetId, to: "assets", a => a.AssetId, verify: true);
                    if (entitlements?.For("s", "readiness") is { } d) t.Entitlement(d);
                })
                .Build();
    }
}
