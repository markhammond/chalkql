using System.Collections.Concurrent;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The <c>bars-akade</c> configuration (D35, D40): the whole corpus, run a second time with
/// <c>bars</c>'s indexes supplied by <c>samples/Chalk.Sample.AkadeIndexedSet</c> and its rows
/// registered as an <see cref="IReadOnlyCollection{T}"/>.
///
/// <para>
/// This is what turns "index creation is pluggable" from a claim into a fact. A structure Chalk did
/// not build answers every range the planner emits, over a source Chalk cannot index into, and every
/// row still has to match the reference executor's — which reads the same table by full scan and
/// evaluates the predicate itself (D39), so it shares nothing with the index at all.
/// </para>
///
/// <para>
/// Plans differ from the built-in configuration's, and are meant to: with no positional order there
/// is no collation to declare, so <c>ORDER BY ts</c> costs a sort here. Results are the contract.
/// </para>
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class AkadeIndexTests(SharedSidecar sidecar)
{
    private static readonly AkadeCorpusFixture Fixture = AkadeCorpusFixture.Shared;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadAll())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_corpus_gives_the_same_answers_with_host_supplied_indexes(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadAll().Single(q => q.Name == name);

        await using var vectorised = await CreateEngineAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateEngineAsync(ExecutionEngine.Reference);

        try
        {
            var actualPrepared = await vectorised.PrepareAsync(query.Sql, query.PrepareOptions());
            var expectedPrepared = await reference.PrepareAsync(
                query.Sql, query.PrepareOptions(PushdownLevel.None));

            var (actual, _, actualPlan) =
                await DifferentialRunner.RunWithPlanAsync(vectorised, actualPrepared, query);
            var (expected, _) = await DifferentialRunner.RunAsync(reference, expectedPrepared, query);

            try
            {
                CorpusDifferentialTests.Compare(
                    $"{query.Name}: bars-akade, vectorised@FULL vs reference@NONE",
                    expected,
                    actual,
                    DifferentialRunner.ComparisonFor(actualPlan, Fixture.Catalog));
            }
            finally
            {
                DifferentialRunner.Dispose(actual);
                DifferentialRunner.Dispose(expected);
            }
        }
        catch (ExecutionException)
        {
            Assert.Fail("The query failed");
        }
    }

    /// <summary>
    /// The extension point has to actually be used, or the comparison above proves only that a full
    /// scan agrees with a full scan.
    /// </summary>
    [Fact]
    public async Task The_host_supplied_index_is_the_one_the_planner_chooses()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync("SELECT * FROM bars WHERE symbol = ?");

        var lookup = PlanWalker.Rels(prepared.Plan)
            .SingleOrDefault(r => r.KindCase == Rel.KindOneofCase.IndexLookup);
        Assert.NotNull(lookup);
        Assert.Equal("ix_bars_symbol_ts", lookup.IndexLookup.Index);

        await using var execution = await engine.ExecuteAsync(prepared, [Utf8String.FromAsciiString("BTCUSDT")]);
        var rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        // A pure lookup reads exactly what it produces, whoever built the index.
        Assert.Equal(Fixtures.BarMinutes, rows);
        Assert.Equal(rows, execution.Stats.RowsScanned);
    }

    /// <summary>
    /// The conformance kit, on the sample (§1). A host-supplied index's descriptor is trusted as
    /// declared, so this is where the claim is checked: every range answered twice, once by the
    /// index and once by a full scan.
    /// </summary>
    [Fact]
    public void The_sample_adapter_conforms()
    {
        foreach (var (index, keys) in Fixture.BarIndexes())
        {
            var report = PocoIndexConformance.Verify(index, Fixture.BarRows, keys);
            Assert.Equal(Fixtures.BarMinutes * Fixtures.Symbols.Length, report.Rows);
            Assert.True(report.Ranges >= 20, $"{report.Index}: only {report.Ranges} ranges checked");
        }
    }

    /// <summary>
    /// The built-in permutation index has to pass the same battery. It would be an odd conformance
    /// kit that only the sample was held to.
    /// </summary>
    [Fact]
    public void The_built_in_index_conforms()
    {
        var fixture = CorpusFixture.Shared;
        var index = fixture.Source.FindIndex<Bar>("bars", "ix_bars_symbol_ts");
        Assert.NotNull(index);

        var report = PocoIndexConformance.Verify(
            index,
            fixture.Bars,
            [b => b.Symbol, b => (b.Ts.Ticks - DateTime.UnixEpoch.Ticks) * 100L]);
        Assert.True(report.Ranges >= 20, $"{report.Index}: only {report.Ranges} ranges checked");
    }

    private readonly ConcurrentDictionary<ExecutionEngine, Lazy<Task<ChalkEngine>>> _engines = new();

    private Task<ChalkEngine> CreateEngineAsync(ExecutionEngine engine) =>
        _engines.GetOrAdd(
            engine,
            static (engine, self) => new(
                () => self.CreateEngineCoreAsync(engine),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;

    private async Task<ChalkEngine> CreateEngineCoreAsync(ExecutionEngine engine) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions
            {
                Engine = engine,
                BatchSize = 4096
            },
        });
}
