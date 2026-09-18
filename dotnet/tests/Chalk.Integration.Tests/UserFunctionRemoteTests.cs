using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using PlanErrorKind = Chalk.Client.Rpc.PlanErrorKind;

namespace Chalk.Integration.Tests;

/// <summary>
/// The two step 22 corpus queries that need a database (§5's 12 and 13), plus the refusal that is
/// the other half of 12. They live here rather than in <c>m6-udf</c> for the reason ADR 0020 gives:
/// a corpus query addresses one catalog, and these want the one with DuckDB behind it.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class UserFunctionRemoteTests(SharedSidecar sidecar)
{
    private static readonly RemoteFixture Fixture = RemoteFixture.Shared;

    /// <summary>
    /// The half of this family the two engines can both run. The reference executor is the I4 oracle
    /// and runs plans made at <c>PushdownLevel.None</c>, where nothing is pushed — and a native
    /// function has no local implementation by construction (D78), so query 12 has no NONE form at
    /// all. It is checked against an independent MD5 below instead.
    /// </summary>
    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM6UdfRemote())
        {
            if (!query.Name.StartsWith("12_", StringComparison.Ordinal))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_remote_udf_query_agrees_with_the_reference_executor(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM6UdfRemote().Single(q => q.Name == name);
        await using var vectorised = await CreateAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateAsync(ExecutionEngine.Reference);

        var preparedFull = await vectorised.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.Full));
        var preparedNone = await reference.PrepareAsync(
            query.Sql, query.PrepareOptions(PushdownLevel.None));

        var (actual, _, plan) = await DifferentialRunner.RunWithPlanAsync(vectorised, preparedFull, query);
        var (expected, _, _) = await DifferentialRunner.RunWithPlanAsync(reference, preparedNone, query);
        try
        {
            PlanExpectations.Check(query, plan);
            ResultComparer.AssertEquivalent(
                expected, actual, DifferentialRunner.ComparisonFor(plan, vectorised.Catalog));
        }
        finally
        {
            DifferentialRunner.Dispose(actual);
            DifferentialRunner.Dispose(expected);
        }
    }

    /// <summary>
    /// §5 corpus 12: the function is visible in the SQL the source is handed, and what comes back is
    /// an MD5 — checked against .NET's own, which is an oracle DuckDB knows nothing about.
    /// </summary>
    [Fact]
    public async Task A_native_function_is_spelled_the_source_s_way_and_answers_what_it_should()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM6UdfRemote().Single(q => q.Name == "12_native_md5_pushed");
        await using var engine = await CreateAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions(PushdownLevel.Full));

        var remote = PlanWalker.Rels(prepared.Plan)
            .Single(r => r.KindCase == Rel.KindOneofCase.RemoteQuery)
            .RemoteQuery;
        Assert.Contains("md5(", remote.QueryText, StringComparison.Ordinal);
        PlanExpectations.Check(query, prepared.Plan);

        var (batches, _, _) = await DifferentialRunner.RunWithPlanAsync(engine, prepared, query);
        try
        {
            var rows = ResultComparer.Rows(batches);
            Assert.NotEmpty(rows);
            var byKey = RemoteFixture.Shared.Poco.Customers.ToDictionary(c => c.CustKey, c => c.Name);
            foreach (var row in rows)
            {
                var key = (long)row[0]!;
                var expected = Convert.ToHexStringLower(
                    System.Security.Cryptography.MD5.HashData(
                        System.Text.Encoding.UTF8.GetBytes(byKey[key])));
                Assert.Equal(expected, (string)row[1]!);
            }
        }
        finally
        {
            DifferentialRunner.Dispose(batches);
        }
    }

    /// <summary>
    /// §5 corpus 12's other half: the same function over a POCO table has nobody to evaluate it, and
    /// the message names the source it belongs to.
    /// </summary>
    [Fact]
    public async Task A_native_function_off_its_source_is_refused_naming_the_source()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync(ExecutionEngine.Vectorised);
        var error = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync("SELECT duck.md5(symbol) FROM bars_small").AsTask());

        Assert.Equal(PlanErrorKind.Unsupported, error.Kind);
        Assert.Contains("cannot be evaluated outside source duck", error.Message, StringComparison.Ordinal);
    }

    private async ValueTask<ChalkEngine> CreateAsync(ExecutionEngine engine) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "remote-udf",
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine },
        });
}
