using System.Globalization;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An operand is evaluated only for the rows its guard leaves to it: an <c>AND</c>'s or an
/// <c>OR</c>'s later operand for the rows the earlier ones left undecided, a <c>CASE</c>'s later
/// condition for the rows no earlier one took. Each case runs on both executors.
/// </summary>
/// <remarks>
/// The vectorised executor skipped a later operand only once every row was decided, and evaluated
/// every <c>CASE</c> condition over every row, so a guard as plain as <c>Divisor &lt;&gt; 0 AND 10 /
/// Divisor &gt; 2</c> divided by zero.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class GuardedOperandTests(SharedSidecar sidecar)
{
    private sealed record Item(int Id, string Code, int Divisor);

    private static readonly Item[] Items = [new(1, "42", 2), new(2, "X", 0), new(3, "7", 5)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ExecutionEngine> Engines => new() { ExecutionEngine.Vectorised, ExecutionEngine.Reference };

    /// <summary>An <c>AND</c>'s later operand divides no row its earlier operand turned away.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task An_and_guards_its_later_operand(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine, "SELECT Id, Divisor <> 0 AND 10 / Divisor > 2 FROM s.items ORDER BY Id");

        Assert.Equal([true, false, false], rows.Select(r => (bool?)r[1]));
    }

    /// <summary>And an <c>OR</c>'s, for the rows its earlier operand did not already answer.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task An_or_guards_its_later_operand(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine, "SELECT Id, Divisor = 0 OR 10 / Divisor > 2 FROM s.items ORDER BY Id");

        Assert.Equal([true, true, false], rows.Select(r => (bool?)r[1]));
    }

    /// <summary>A <c>CASE</c>'s later condition is evaluated for no row an earlier one took.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_later_case_condition_is_guarded_by_the_earlier_ones(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine,
            "SELECT Id, CASE WHEN Divisor = 0 THEN 0 WHEN 10 / Divisor > 2 THEN 1 ELSE 2 END FROM s.items ORDER BY Id");

        Assert.Equal([1, 0, 2], rows.Select(r => Convert.ToInt32(r[1], CultureInfo.InvariantCulture)));
    }

    private async Task<ChalkEngine> EngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "guarded-operands",
            Sources =
            [
                new PocoSourceBuilder("s", "s")
                    .AddTable("items", Items, t => t.UniqueKey(x => x.Id))
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        }, Ct);

    private static async Task<List<object?[]>> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<object?[]>();
        await using var execution = await engine.ExecuteAsync(await engine.PrepareAsync(sql, ct: Ct), ct: Ct);
        await foreach (var batch in execution.Batches.WithCancellation(Ct))
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows());
            }
        }

        return rows;
    }
}
