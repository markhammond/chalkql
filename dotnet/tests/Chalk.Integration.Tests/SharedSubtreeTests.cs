using System.Globalization;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A <c>WITH</c> block computed once (D338, design 63 §2, F180). Calcite inlines a block at each
/// reference and the IR is a tree, so each copy ran, and read its sources again. The planner now
/// writes a repeated subtree once, in the plan's shared list, and every use reads the same rows: the
/// three branches of this <c>UNION ALL</c>, each totalling one block a different way, read the fills
/// once. Both engines answer.
/// </summary>
public sealed class SharedSubtreeTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private const string ThreeTotals =
        """
        WITH p AS (SELECT asset, venue, SUM(qty) AS qty FROM fills GROUP BY asset, venue)
        SELECT 'asset' AS level, asset AS name, SUM(qty) AS qty FROM p GROUP BY asset
        UNION ALL
        SELECT 'venue', venue, SUM(qty) FROM p GROUP BY venue
        UNION ALL
        SELECT 'all', 'all', SUM(qty) FROM p
        """;

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_block_read_three_times_is_computed_once(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(new ExecutionOptions { Engine = executor });

        var prepared = await engine.PrepareAsync(ThreeTotals);
        var (rows, scanned) = await RowsAsync(engine, prepared);

        Assert.Single(prepared.Plan.Shared);
        Assert.Equal(3, PlanWalker.Rels(prepared.Plan.Root).Count(r => r.KindCase == Rel.KindOneofCase.SharedRef));
        Assert.Equal(Fills.Length, scanned);
        Assert.Equal(Expected(), rows.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The shared rows are held in the execution's arena, under its budget: past it, the execution
    /// fails as an over-budget arena does anywhere, naming where it broke.
    /// </summary>
    [Fact]
    public async Task A_shared_block_is_held_under_the_arenas_budget()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(
            new ExecutionOptions { Arena = new ArenaOptions { MaxBytes = 4096 } });

        var prepared = await engine.PrepareAsync(ThreeTotals);
        var error = await Assert.ThrowsAsync<ArenaBudgetExceededException>(() => RowsAsync(engine, prepared));

        Assert.Equal(4096, error.MaxBytes);
        Assert.Matches(@"^Execution failed at root\S* \(plan [0-9a-f]{16}\): ", error.Message);
    }

    /// <summary>Two thousand fills over forty assets and three venues.</summary>
    private static readonly Fill[] Fills =
    [
        .. Enumerable.Range(0, 2_000).Select(i => new Fill(
            i,
            $"A{i % 40:D2}",
            (i % 3) switch { 0 => "LMAX", 1 => "EBS", _ => "CBOE" },
            i % 7)),
    ];

    private static string[] Expected()
    {
        var rows = new List<string>();
        rows.AddRange(Fills.GroupBy(f => f.Asset).Select(g => $"asset|{g.Key}|{g.Sum(f => f.Qty)}"));
        rows.AddRange(Fills.GroupBy(f => f.Venue).Select(g => $"venue|{g.Key}|{g.Sum(f => f.Qty)}"));
        rows.Add($"all|all|{Fills.Sum(f => f.Qty)}");
        return [.. rows.Order(StringComparer.Ordinal)];
    }

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionOptions execution) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "shared-subtree",
            Sources =
            [
                new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("fills", Fills).Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = execution,
        });

    private static async Task<(List<string> Rows, long Scanned)> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                var values = new string[batch.ColumnCount];
                for (var c = 0; c < batch.ColumnCount; c++)
                {
                    values[c] = batch.Column(c) switch
                    {
                        StringArray text => text.GetString(i),
                        StringViewArray view => view.GetString(i),
                        Int64Array longs => longs.GetValue(i)!.Value.ToString(CultureInfo.InvariantCulture),
                        Int32Array ints => ints.GetValue(i)!.Value.ToString(CultureInfo.InvariantCulture),
                        var other => other.GetType().Name,
                    };
                }

                rows.Add(string.Join('|', values));
            }

            batch.Dispose();
        }

        return (rows, execution.Stats.RowsScanned);
    }

    public sealed record Fill(int Id, string Asset, string Venue, long Qty);
}
