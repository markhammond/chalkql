using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The plans an entitled statement takes after its prepare (design 69 §4): one a background run made
/// and one for another length of a list parameter. Each is checked as the prepare's was, against the
/// report it came with, and must come with the report the statement holds, which its labels and its
/// audit describe. A background plan that fails is not taken; a list length that fails refuses the
/// execution.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class EntitledPlansTests(SharedSidecar sidecar)
{
    private sealed record Fill(int Seq, int Book, long Qty, int OrgId);

    private sealed record Book(int Id, int Desk);

    private const string ByDesk =
        "SELECT b.desk, SUM(f.qty) AS qty FROM fills AS f JOIN books AS b ON b.id = f.book GROUP BY b.desk";

    /// <summary>A reader of the fills of organizations 1 and 2; those of organization 0 are hidden.</summary>
    private static readonly RequestContext Reader = TenancyFixture.Principal(
        user: 1, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: [], allowedOrgs: [1, 2]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An entitled statement prepared on empty tables takes the plan a background run made for the
    /// loaded ones, at the refresh after it: the same report, the same rows, and an audit event that
    /// names the plan that ran.
    /// </summary>
    [Fact]
    public async Task An_entitled_statement_takes_a_background_plan_under_the_same_report()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var audit = new RecordingAudit();
        await using var desk = await DeskAsync(new TamperingPlanner(sidecar.CreatePlanner()));
        var entitled = await desk.Engine.WithEntitlements(audit: audit).PrepareAsync(ByDesk, Reader, ct: Ct);
        var planned = entitled.PlanDigest;
        var report = entitled.Entitlements;

        await desk.LoadAsync(Fills(5_000, books: 50), Books(50));
        await desk.Engine.BackgroundPlanningSettledAsync();
        var rows = await RowsAsync(desk.Engine, entitled);
        Assert.Equal(planned, audit.Events[^1].PlanDigest);

        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        Assert.NotEqual(planned, entitled.PlanDigest);
        Assert.Same(report, entitled.Entitlements);
        Assert.False(entitled.Query.IsStale);
        Assert.Equal(rows, await RowsAsync(desk.Engine, entitled));
        Assert.Equal(entitled.PlanDigest, audit.Events[^1].PlanDigest);
        Assert.Equal(report.DescriptorHashes, audit.Events[^1].DescriptorHashes);
    }

    /// <summary>
    /// A background plan whose report differs from the statement's, here in whether a row predicate
    /// reached its source, is not taken: the statement keeps its plan and report. A later run that
    /// brings a plan under the same report is taken.
    /// </summary>
    [Fact]
    public async Task A_background_plan_under_another_report_is_not_taken()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new TamperingPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner);
        var entitled = await desk.Engine.WithEntitlements().PrepareAsync(ByDesk, Reader, ct: Ct);
        var planned = entitled.PlanDigest;

        planner.Tamper = true;
        await desk.LoadAsync(Fills(5_000, books: 50), Books(50));
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);

        Assert.Equal(planned, entitled.PlanDigest);
        Assert.True(entitled.Query.IsStale);

        // The run the last refresh asked for may plan before the switch; the one after it does not.
        planner.Tamper = false;
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        await desk.Engine.BackgroundPlanningSettledAsync();
        await desk.Engine.RefreshAsync(r => r.DeferPlanning(), Ct);
        Assert.NotEqual(planned, entitled.PlanDigest);
    }

    /// <summary>
    /// A list parameter's other lengths are planned at their first execution and checked then: a plan
    /// that comes with the statement's report runs; one that comes with another refuses the execution,
    /// naming what differs, and is not kept, so the length is planned again next time.
    /// </summary>
    [Fact]
    public async Task A_list_length_planned_at_its_first_execution_is_checked()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new TamperingPlanner(sidecar.CreatePlanner());
        await using var desk = await DeskAsync(planner);
        await desk.Engine.RefreshAsync(r => r.Replace(desk.Source, "fills", Fills(60, books: 6)), Ct);
        var entitled = await desk.Engine.WithEntitlements()
            .PrepareAsync("SELECT seq FROM fills WHERE book IN @books ORDER BY seq", Reader, ct: Ct);

        Assert.Equal(20, await CountAsync(desk.Engine, entitled, [1, 2, 3]));

        planner.Tamper = true;
        var refused = await Assert.ThrowsAsync<Chalk.Ir.InvalidPlanException>(
            () => CountAsync(desk.Engine, entitled, [1, 2, 3, 4]));
        Assert.Equal(ChalkErrorCodes.PlanBreaksEntitlements, refused.Code);
        Assert.Contains("came with another entitlements report", refused.Message, StringComparison.Ordinal);
        Assert.Contains("main.fills's row predicate reaches its source", refused.Message, StringComparison.Ordinal);

        planner.Tamper = false;
        Assert.Equal(30, await CountAsync(desk.Engine, entitled, [1, 2, 3, 4]));
    }

    // ---- the parts ----

    /// <summary>Fills of organizations 0, 1 and 2 in turn; the reader sees two in three.</summary>
    private static IReadOnlyList<Fill> Fills(int count, int books) =>
        [.. Enumerable.Range(1, count).Select(i => new Fill(i, i % books, (i % 7) + 1, i % 3))];

    private static IReadOnlyList<Book> Books(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % 3))];

    private static async Task<Dictionary<long, long>> RowsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var rows = new Dictionary<long, long>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                        Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
                }
            }
        }

        return rows;
    }

    private static async Task<int> CountAsync(ChalkEngine engine, PreparedQuery query, int[] books)
    {
        var rows = 0;
        await using var execution = await engine.ExecuteAsync(
            query, new Dictionary<string, object?> { ["books"] = books }, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows += batch.Length;
            }
        }

        return rows;
    }

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required PocoSource Source { get; init; }

        /// <summary>Replaces both tables in one refresh that defers its planning.</summary>
        public async Task LoadAsync(IReadOnlyList<Fill> fills, IReadOnlyList<Book> books) =>
            await Engine.RefreshAsync(
                refresh => refresh.Replace(Source, "fills", fills).Replace(Source, "books", books).DeferPlanning(),
                Ct);

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private static async Task<Desk> DeskAsync(IQueryPlanner planner)
    {
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "fills",
                (IReadOnlyList<Fill>)[],
                t => t.Entitlement(new TableEntitlementDescriptor { RowPredicate = "org_id IN (@ctx.allowed_orgs)" }))
            .AddTable("books", (IReadOnlyList<Book>)[])
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = planner }, Ct);
        return new Desk { Engine = engine, Source = source };
    }

    private sealed class RecordingAudit : IEntitlementsAudit
    {
        public List<EntitlementsAuditEvent> Events { get; } = [];

        public void Executed(EntitlementsAuditEvent audited) => Events.Add(audited);
    }

    /// <summary>
    /// The sidecar, with a switch that makes every plan come back with its entitlements report saying
    /// the first table's row predicate went the other way: a report that differs from the one a
    /// statement holds, as a database source pushing a predicate differently would make it.
    /// </summary>
    private sealed class TamperingPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        private volatile bool _tamper;

        public bool Tamper
        {
            get => _tamper;
            set => _tamper = value;
        }

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

        public ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public async ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            var result = await inner.PlanAsync(request, ct).ConfigureAwait(false);
            if (!_tamper)
            {
                return result;
            }

            var extensions = result.Extensions.Select(extension =>
            {
                if (!extension.Is(Chalk.Entitlements.Rpc.EntitlementsReport.Descriptor))
                {
                    return extension;
                }

                var report = extension.Unpack<Chalk.Entitlements.Rpc.EntitlementsReport>();
                report.Tables[0].RowPredicatePushed = !report.Tables[0].RowPredicatePushed;
                return Google.Protobuf.WellKnownTypes.Any.Pack(report);
            }).ToList();

            return new PlanResult
            {
                Plan = result.Plan,
                LogicalDigest = result.LogicalDigest,
                PlanText = result.PlanText,
                RedactedSql = result.RedactedSql,
                Stats = result.Stats,
                PlanningState = result.PlanningState,
                Extensions = extensions,
            };
        }

        public ValueTask<RedactedSql> RedactSqlAsync(RedactSqlRequest request, CancellationToken ct = default) =>
            inner.RedactSqlAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
