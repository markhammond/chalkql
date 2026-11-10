using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The explanation's expressions are ChalkQL SQL over the names a host knows (D207, F286): a table's
/// columns by name, literals as SQL writes them, an unbound context value as <c>@ctx.name</c>.
/// </summary>
/// <remarks>
/// They were the planner's own form: <c>=($1, 'KESTREL')</c>, columns by position, literals with
/// their types (<c>'KESTREL':VARCHAR CHARACTER SET "UTF-8"</c>), a stand-in as
/// <c>WHEN true THEN null:VARCHAR CHARACTER SET "UTF-8"</c>. A host that showed an explanation parsed
/// it back into SQL itself. The expressions are the same folded ones the pass enforces; only how they
/// are said changed.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ExplainedSqlTests(SharedSidecar sidecar)
{
    private sealed record Report(string ReportId, string MissionId, string Assessment, string Location, bool Open, string User);

    private static readonly Report[] Reports =
    [
        new("R1", "KESTREL", "Unresolved activity.", "GRID 111", true, "ann"),
        new("R2", "SENTINEL", "Routine.", "GRID 222", false, "bob"),
    ];

    private const string Statement = "SELECT ReportId, Assessment, Location, \"Open\", \"User\" FROM s.reports";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The row predicate names the column and gives its literals as SQL does: one mission is an
    /// equality, two are an <c>IN</c> list.
    /// </summary>
    [Fact]
    public async Task The_row_predicate_names_its_columns()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);

        Assert.Equal("MissionId = 'KESTREL'", (await fixture.TableAsync("KESTREL")).RowPredicate);
        Assert.Equal("MissionId IN ('KESTREL', 'SENTINEL')", (await fixture.TableAsync("KESTREL", "SENTINEL")).RowPredicate);
    }

    /// <summary>
    /// A mask template is the column's own name where its parameter stood, and a stand-in every row
    /// takes is that stand-in, not a condition that always holds.
    /// </summary>
    [Fact]
    public async Task A_mask_and_a_stand_in_are_said_as_sql()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);

        var columns = (await fixture.TableAsync("KESTREL")).Columns.ToDictionary(c => c.Column);

        Assert.Equal("SUBSTRING(Assessment, 1, 1)", columns["Assessment"].Mask);
        Assert.Equal("REDACTED", columns["Location"].Disclosure);
        Assert.Equal("'withheld'", columns["Location"].Placeholder);
    }

    /// <summary>
    /// A column whose name SQL reserves is quoted where it is named, so the text reads it as the
    /// column: <c>"Open"</c>, and <c>"User"</c>, which unquoted would be SQL's <c>USER</c>.
    /// </summary>
    [Fact]
    public async Task A_reserved_column_name_is_quoted()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);

        var columns = (await fixture.TableAsync("KESTREL", "SENTINEL")).Columns.ToDictionary(c => c.Column);

        // Each condition holds the reader's role, over the mission, and the rule's own When.
        Assert.Contains(" AND \"Open\" THEN FULL", columns["ReportId"].Disclosure, StringComparison.Ordinal);
        Assert.Contains(" AND \"User\" = 'ann' THEN FULL", columns["Assessment"].Disclosure, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", columns["Assessment"].Disclosure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan prepared once for a shape is explained with the context it leaves open named as a
    /// statement names it: <c>@ctx.</c><i>list</i>.
    /// </summary>
    [Fact]
    public async Task An_open_context_list_is_named_as_a_statement_names_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar);

        var prepared = await fixture.Entitled.PrepareAsync(Statement, fixture.Bind("KESTREL").Shape(), ct: Ct);
        var table = Assert.Single((await prepared.ExplainAsync(Ct)).Tables);

        Assert.Contains("@ctx.", table.RowPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("$", table.RowPredicate, StringComparison.Ordinal);
        Assert.StartsWith("MissionId IN (@ctx.", table.RowPredicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports kept by mission. A reader sees a report's id only while it is open, its assessment in
    /// full where they filed it and as an initial elsewhere, and never its location, which is
    /// withheld.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private TenancyEntitlements _entitlements = null!;
        private Kind _mission;
        private Role _reader;

        public ChalkEngine Engine { get; private set; } = null!;

        public EntitledEngine Entitled { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(SharedSidecar sidecar)
        {
            var fixture = new Fixture();
            var catalog = new CatalogContext { ContextId = "explained-sql", Schemas = [Source(null).DescribeSchema()] };
            var policy = TenancyPolicy.Declare(catalog);
            fixture._mission = policy.Tenancy("mission");
            fixture._reader = policy.Role("reader");
            var reader = fixture._reader;
            var table = policy.Source("s").Table("reports");
            table.Tenancy(t => t.Direct(fixture._mission, table.Column("MissionId")));
            table.Access(new AccessRule { Roles = [reader], Column = table.Column("ReportId"), Grants = Verdict.Full, When = Sql.Of("\"Open\"") });
            table.Access(new AccessRule { Roles = [reader], Column = table.Column("ReportId"), Grants = Verdict.None });
            table.Access(new AccessRule { Roles = [reader], Column = table.Column("Assessment"), Grants = Verdict.Full, When = Sql.Of("\"User\" = 'ann'") });
            table.Access(new AccessRule
            {
                Roles = [reader], Column = table.Column("Assessment"), Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
            });
            table.Access(new AccessRule
            {
                Roles = [reader], Column = table.Column("Location"), Grants = Verdict.None, Placeholder = Sql.Of("'withheld'"),
            });
            fixture._entitlements = policy.Compile(catalog);

            fixture.Engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "explained-sql",
                Sources = [Source(fixture._entitlements)],
                Planner = sidecar.CreatePlanner(),
            }, Ct);
            fixture.Entitled = fixture.Engine.WithEntitlements();
            return fixture;
        }

        public RequestContext Bind(params string[] missions) => _entitlements.Bind(new TenancyPrincipal
        {
            User = "u",
            Grants = [.. missions.Select(m => Grant.ForTenancy(_mission, m, _reader))],
        });

        public async Task<ExplainedTable> TableAsync(params string[] missions) =>
            Assert.Single((await Entitled.ExplainAsync(Statement, Bind(missions), ct: Ct)).Tables);

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
