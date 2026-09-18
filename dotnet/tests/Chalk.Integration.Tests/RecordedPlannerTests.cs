using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using PlanRequest = Chalk.Client.PlanRequest;

namespace Chalk.Integration.Tests;

/// <summary>
/// Invariant I3: the transport is swappable, so executor tests and the corpus can run with no
/// sidecar at all. These tests need no JVM — they read what
/// <c>scripts/record-plans.sh</c> already wrote.
/// </summary>
public sealed class RecordedPlannerTests
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.Load())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_corpus_query_can_be_served_from_the_recorded_plans(string name)
    {
        var query = CorpusQueries.Load().Single(q => q.Name == name);
        await using var planner = new RecordedPlanner(RepoLayout.Plans.FullName);
        await planner.RegisterCatalogAsync(new CatalogRegistration { Catalog = Fixture.Catalog });

        foreach (var level in new[] { PushdownLevel.Full, PushdownLevel.None })
        {
            var result = await planner.PlanAsync(new PlanRequest
            {
                Sql = PlannerSqlFor(query),
                ContextId = Fixture.Catalog.ContextId,
                CatalogEpoch = Fixture.Catalog.Epoch,
                Options = new Chalk.Client.PlannerOptions
                {
                    Pushdown = level,
                    Conformance = query.Conformance,
                },
            });

            PlanValidator.Validate(result.Plan);
            Assert.Equal(
                PlanDigest.Format(RecordedDigest(name, level == PushdownLevel.Full ? "full" : "none")),
                PlanDigest.Format(result.PlanDigest));
        }
    }

    /// <summary>
    /// A whole engine, planned entirely from the recorded corpus. This is what makes an executor test
    /// runnable on a laptop with no JDK.
    /// </summary>
    [Fact]
    public async Task An_engine_can_be_built_on_recorded_plans_alone()
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
        });

        // A corpus query, because only recorded SQL can be planned: the ORDER BY is the table's own
        // collation, so a plan that came back at all also proves the recorded plan is the optimised one.
        var query = CorpusQueries.Load().Single(q => q.Name == "04_order_by_collated");
        var prepared = await engine.PrepareAsync(query.Sql);

        Assert.Equal(
            ["symbol", "ts", "open", "high", "low", "close", "volume", "vwap", "trade_count"],
            prepared.OutputSchema.FieldsList.Select(f => f.Name));
        Assert.False(PlanWalker.Has(prepared.Plan, Rel.KindOneofCase.Sort));
    }

    /// <summary>
    /// D250: a directory of recorded plans carries no sidecar capability probe to replay, so
    /// <see cref="PlannerInfo.Dialects"/>, <see cref="PlannerInfo.Conformances"/> and
    /// <see cref="PlannerInfo.Libraries"/> come back empty rather than guessed — the same honesty
    /// <see cref="PlannerInfo.PlannerConfigHash"/> already carries as a constant zero here.
    /// </summary>
    [Fact]
    public async Task GetInfo_carries_empty_dialect_conformance_and_library_lists()
    {
        await using var planner = new RecordedPlanner(RepoLayout.Plans.FullName);

        var info = await planner.GetInfoAsync();

        Assert.Empty(info.Dialects);
        Assert.Empty(info.Conformances);
        Assert.Empty(info.Libraries);
    }

    /// <summary>A plan that was never recorded fails with the command that records it.</summary>
    [Fact]
    public async Task A_missing_plan_names_the_command_that_would_record_it()
    {
        await using var planner = new RecordedPlanner(RepoLayout.Plans.FullName);
        await planner.RegisterCatalogAsync(new CatalogRegistration { Catalog = Fixture.Catalog });

        var error = await Assert.ThrowsAsync<RecordedPlanMissingException>(
            () => planner.PlanAsync(new PlanRequest
            {
                Sql = "SELECT symbol FROM bars WHERE symbol = 'never planned before'",
                ContextId = Fixture.Catalog.ContextId,
                CatalogEpoch = Fixture.Catalog.Epoch,
            }).AsTask());

        Assert.Contains("scripts/record-plans.sh", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A recorded plan belongs to a catalog whose shape it still fits. Serving it against a catalog
    /// in which a table it reads has changed is exactly the wrong-answer bug this check prevents —
    /// and since D271 (a) the comparison is by shape and never by an epoch or a minted version,
    /// because a version is new in every process and a recording keyed on one could never replay.
    /// </summary>
    [Fact]
    public async Task A_recorded_plan_is_refused_against_a_changed_table_shape()
    {
        await using var planner = new RecordedPlanner(RepoLayout.Plans.FullName);
        await planner.RegisterCatalogAsync(
            new CatalogRegistration { Catalog = Renamed(Fixture.Catalog, "bars", "symbol", "ticker") });

        var stale = await Assert.ThrowsAsync<Chalk.Sources.StalePlanException>(
            () => planner.PlanAsync(new PlanRequest
            {
                Sql = PlannerSqlFor(CorpusQueries.Load().First()),
                ContextId = Fixture.Catalog.ContextId,
                CatalogEpoch = Fixture.Catalog.Epoch,
            }).AsTask());

        Assert.Equal("main.bars", stale.Table);
    }

    /// <summary>
    /// And a plain epoch bump is not a shape change, so the same recorded plan is served (D271 (b)):
    /// the epoch counts refreshes and says nothing about whether this plan still fits.
    /// </summary>
    [Fact]
    public async Task A_recorded_plan_survives_an_epoch_that_moved_without_the_shape()
    {
        await using var planner = new RecordedPlanner(RepoLayout.Plans.FullName);
        await planner.RegisterCatalogAsync(new CatalogRegistration
        {
            Catalog = new Chalk.Catalog.CatalogContext
            {
                ContextId = Fixture.Catalog.ContextId,
                Epoch = 99,
                Schemas = Fixture.Catalog.Schemas,
            },
        });

        var result = await planner.PlanAsync(new PlanRequest
        {
            Sql = PlannerSqlFor(CorpusQueries.Load().First()),
            ContextId = Fixture.Catalog.ContextId,
            CatalogEpoch = 99,
        });

        Assert.NotNull(result.Plan);
    }

    /// <summary>The same catalog with one column of one table renamed, which is a shape change.</summary>
    private static Chalk.Catalog.CatalogContext Renamed(
        Chalk.Catalog.CatalogContext catalog, string table, string from, string to) =>
        new()
        {
            ContextId = catalog.ContextId,
            Epoch = catalog.Epoch,
            Associations = catalog.Associations,
            JoinPolicy = catalog.JoinPolicy,
            Schemas =
            [
                .. catalog.Schemas.Select(schema => new Chalk.Catalog.SchemaDescriptor
                {
                    SourceId = schema.SourceId,
                    Name = schema.Name,
                    Kind = schema.Kind,
                    Dialect = schema.Dialect,
                    Capabilities = schema.Capabilities,
                    DialectProfile = schema.DialectProfile,
                    CostProfile = schema.CostProfile,
                    Functions = schema.Functions,
                    TrustSourceRowSecurity = schema.TrustSourceRowSecurity,
                    Tables =
                    [
                        .. schema.Tables.Select(t =>
                            !string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)
                                ? t
                                : new Chalk.Catalog.TableDescriptor
                                {
                                    Name = t.Name,
                                    RowCount = t.RowCount,
                                    RowCountKind = t.RowCountKind,
                                    CostProfile = t.CostProfile,
                                    UniqueKeys = t.UniqueKeys,
                                    Collations = t.Collations,
                                    Indexes = t.Indexes,
                                    Entitlement = t.Entitlement,
                                    ForeignKeys = t.ForeignKeys,
                                    Partitioning = t.Partitioning,
                                    Columns =
                                    [
                                        .. t.Columns.Select(c =>
                                            !string.Equals(c.Name, from, StringComparison.OrdinalIgnoreCase)
                                                ? c
                                                : new Chalk.Catalog.ColumnDescriptor
                                                {
                                                    Name = to,
                                                    Type = c.Type,
                                                    Statistics = c.Statistics,
                                                }),
                                    ],
                                }),
                    ],
                }),
            ],
        };

    private static string PlannerSqlFor(CorpusQuery query)
    {
        var rewriter = ParameterRewriter.Parse(query.Sql);
        return rewriter.Render(rewriter.PrepareShape()).Sql;
    }

    private static ulong RecordedDigest(string name, string level) =>
        PlanDigest.Parse(File.ReadAllText(Path.Combine(RepoLayout.Plans.FullName, $"{name}.{level}.digest")));
}
