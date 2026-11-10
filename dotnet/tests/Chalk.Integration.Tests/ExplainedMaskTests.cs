using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The explanation's mask is the one the statement applies (D207): a rule's own mask, else the
/// column's, and none where the column is not masked.
/// </summary>
/// <remarks>
/// It gave the column's own mask, the policy's default <c>'********'</c> for a string, whatever a
/// rule declared, and gave it too where a principal's rules left the column in full. A page built on
/// the explanation told its reader a mask the statement never applied.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ExplainedMaskTests(SharedSidecar sidecar)
{
    private sealed record Report(string ReportId, string MissionId, string Assessment, string SourceIdentity, string Note);

    private static readonly Report[] Reports =
    [
        new("R1", "KESTREL", "Unresolved activity.", "SOURCE-01", "kestrel note"),
        new("R2", "SENTINEL", "Routine.", "SOURCE-02", "sentinel note"),
    ];

    private const string Statement = "SELECT ReportId, Assessment, SourceIdentity, Note FROM s.reports ORDER BY ReportId";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An analyst's masks are the rules' own: a literal, and a template filled in for the column. The
    /// values the statement returns are those masks.
    /// </summary>
    [Fact]
    public async Task A_rules_own_mask_is_the_one_explained()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);
        var context = fixture.Bind(fixture.Analyst);

        var columns = await fixture.ColumnsAsync(context);

        Assert.Equal("'WITHHELD'", columns["SourceIdentity"].Mask);
        Assert.Contains("SUBSTRING", columns["Assessment"].Mask, StringComparison.Ordinal);
        Assert.DoesNotContain("********", columns["Assessment"].Mask, StringComparison.Ordinal);
        Assert.Equal("", columns["ReportId"].Mask);

        var rows = await fixture.RowsAsync(context);
        Assert.Equal(["WITHHELD", "WITHHELD"], rows.Select(r => r[2]));
        Assert.Equal(["U", "R"], rows.Select(r => r[1]));
    }

    /// <summary>A column the principal's rules leave in full has no mask to explain.</summary>
    [Fact]
    public async Task A_column_left_in_full_explains_no_mask()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);
        var context = fixture.Bind(fixture.Analyst, fixture.Commander);

        var columns = await fixture.ColumnsAsync(context);

        Assert.Equal("FULL", columns["SourceIdentity"].Disclosure);
        Assert.Equal("", columns["SourceIdentity"].Mask);
        Assert.Equal(["SOURCE-01", "SOURCE-02"], (await fixture.RowsAsync(context)).Select(r => r[2]));
    }

    /// <summary>
    /// Masks that vary by row are explained as the rules that choose them, in order, in the shape the
    /// disclosure uses: a mission's own mask for its rows, and another for the rest.
    /// </summary>
    [Fact]
    public async Task Masks_that_vary_by_row_are_explained_by_their_conditions()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);
        var context = fixture.Bind(fixture.Analyst);

        var note = (await fixture.ColumnsAsync(context))["Note"];

        Assert.StartsWith("WHEN ", note.Mask, StringComparison.Ordinal);
        Assert.EndsWith(" THEN 'K'; ELSE 'S'", note.Mask, StringComparison.Ordinal);
        Assert.Equal(["K", "S"], (await fixture.RowsAsync(context)).Select(r => r[3]));
    }

    /// <summary>
    /// Reports kept by mission. An analyst sees the source masked by a literal and the assessment as
    /// its initial, and a note masked one way in KESTREL and another elsewhere; a commander sees the
    /// source in full. Principals hold their roles in both missions.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private TenancyEntitlements _entitlements = null!;
        private Kind _mission;

        public ChalkEngine Engine { get; private set; } = null!;

        public EntitledEngine Entitled { get; private set; } = null!;

        public Role Analyst { get; private set; }

        public Role Commander { get; private set; }

        public static async Task<Fixture> CreateAsync(SharedSidecar sidecar)
        {
            var fixture = new Fixture();
            var catalog = new CatalogContext { ContextId = "explained-mask", Schemas = [Source(null).DescribeSchema()] };
            var policy = TenancyPolicy.Declare(catalog);
            fixture._mission = policy.Tenancy("mission");
            fixture.Analyst = policy.Role("analyst");
            fixture.Commander = policy.Role("commander");
            var (analyst, commander) = (fixture.Analyst, fixture.Commander);
            var table = policy.Source("s").Table("reports");
            table.Tenancy(t => t.Direct(fixture._mission, table.Column("MissionId")));
            table.Access(new AccessRule { Roles = [commander], Column = table.Column("SourceIdentity"), Grants = Verdict.Full });
            table.Access(new AccessRule
            {
                Roles = [analyst], Column = table.Column("SourceIdentity"), Grants = Verdict.Mask, Mask = Sql.Of("'WITHHELD'"),
            });
            table.Access(new AccessRule
            {
                Roles = [analyst, commander], Column = table.Column("Assessment"), Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
            });
            table.Access(new AccessRule
            {
                Roles = [analyst], Column = table.Column("Note"), Grants = Verdict.Mask,
                When = Sql.Of("MissionId = 'KESTREL'"), Mask = Sql.Of("'K'"),
            });
            table.Access(new AccessRule
            {
                Roles = [analyst], Column = table.Column("Note"), Grants = Verdict.Mask, Mask = Sql.Of("'S'"),
            });
            fixture._entitlements = policy.Compile(catalog);

            fixture.Engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "explained-mask",
                Sources = [Source(fixture._entitlements)],
                Planner = sidecar.CreatePlanner(),
            }, Ct);
            fixture.Entitled = fixture.Engine.WithEntitlements();
            return fixture;
        }

        public RequestContext Bind(params Role[] roles) => _entitlements.Bind(new TenancyPrincipal
        {
            User = "u",
            Grants = [.. roles.SelectMany(role => new[] { Grant.ForTenancy(_mission, "KESTREL", role), Grant.ForTenancy(_mission, "SENTINEL", role) })],
        });

        public async Task<Dictionary<string, ExplainedColumn>> ColumnsAsync(RequestContext context)
        {
            var explained = await Entitled.ExplainAsync(Statement, context, ct: Ct);
            return Assert.Single(explained.Tables).Columns.ToDictionary(c => c.Column);
        }

        public async Task<List<object?[]>> RowsAsync(RequestContext context)
        {
            var prepared = await Entitled.PrepareAsync(Statement, context, ct: Ct);
            var rows = new List<object?[]>();
            await using var execution = await Engine.ExecuteAsync(prepared.Query, ct: Ct);
            await foreach (var batch in execution.Batches.WithCancellation(Ct))
            {
                using (batch)
                {
                    rows.AddRange(batch.ToRows());
                }
            }

            return rows;
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();

        private static PocoSource Source(TenancyEntitlements? entitlements) =>
            new PocoSourceBuilder("s", "s")
                .AddTable("reports", Reports, t =>
                {
                    t.UniqueKey(x => x.ReportId);
                    if (entitlements?.For("s", "reports") is { } d) t.Entitlement(d);
                })
                .Build();
    }
}
