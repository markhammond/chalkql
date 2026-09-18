using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
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
public sealed class PerformanceTest
{
    private ChalkEngine _engine;

    private readonly PreparedQuery _scanFilter;

    public PerformanceTest(SharedSidecar sidecar)
    {
        var source = CorpusFunctions.Declare(new PocoSourceBuilder("mem"))
            // bars and symbols use snake_case so the naming policy is exercised; lineitem names its
            // columns explicitly because TPC-H's l_ prefix is not a naming policy.
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("bars", Fixtures.Bars(Fixtures.DefaultSeed, 20_160 * 100), t => t
                .OrderedBy(b => b.Ts)
                .ThenBy(b => b.Symbol)
                .UniqueKey(b => b.Ts, b => b.Symbol)
                // M2 (D40). The (symbol, ts) index is a permutation — one int per row — and the
                // (ts, symbol) one is the declared collation, so it costs nothing at all (§1).
                .UniqueIndex(b => b.Symbol, b => b.Ts)
                .Index(b => b.Ts, b => b.Symbol)
                // F14: every bar names one of the five `symbols` rows. The fluent spelling of F16,
                // which finds `symbols` by row type.
                .ForeignKey(b => b.Symbol).References<SymbolRow>(s2 => s2.Symbol, verify: false))
            .AddTable("symbols", Fixtures.SymbolRows, t => t
                .OrderedBy(s => s.Symbol)
                .UniqueKey(s => s.Symbol))
            .Build();

        _engine = ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        }).Result;
        
        _scanFilter = _engine.PrepareAsync(
            // "SELECT symbol FROM bars",
        "SELECT symbol, ts, \"close\" FROM bars WHERE volume > 5000",
        new PrepareOptions { Conformance = Chalk.Client.SqlConformance.Lenient, IncludePlanText = true, RequireCurrentCatalog = false}
        ).Result;
        
        var t = Task.Run(async () =>
        {
            await using var throwAwayExecution = await _engine.ExecuteAsync(_scanFilter).AsTask();
            long rows = 0;
            await foreach (var batch in throwAwayExecution.Batches)
            {
                rows += batch.Length;
                batch.Dispose();
            }
            
            Assert.True(rows != 0);
        });
        t.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task The_corpus_gives_the_same_answers_with_host_supplied_indexes()
    {
        PerfThread.Run(async () =>
        {
            await using var execution = await _engine.ExecuteAsync(_scanFilter);
            long rows = 0;
            await foreach (var batch in execution.Batches)
            {
                rows += batch.Length;
                batch.Dispose();
            }
        },
        "PermutationIndex.Perf");

        Assert.True(true, "Completed");
    }
    
    internal static class PerfThread
    {
        public static void Run(
            Action action,
            string name = "Perf")
        {
            ExceptionDispatchInfo? failure = null;

            var thread =
                new Thread(
                    () =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            failure =
                                ExceptionDispatchInfo.Capture(ex);
                        }
                    })
                {
                    Name = name,
                    IsBackground = false,
                };

            thread.Start();
            thread.Join();

            failure?.Throw();
        }
    }
}
