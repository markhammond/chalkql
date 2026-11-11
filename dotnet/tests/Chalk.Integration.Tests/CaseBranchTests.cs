using Chalk.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A <c>CASE</c> branch is evaluated for the rows that take it and no other (F289), as SQL means:
/// a row whose condition is false raises nothing in the branch it does not take.
/// </summary>
/// <remarks>
/// The vectorised executor evaluated every branch over every row and then chose per row, so a
/// guard did not guard: <c>CASE WHEN Code = '42' THEN CAST(Code AS DOUBLE) ELSE 0 END</c> failed on
/// a row whose code was not a number, quoting it. Each case runs on both executors.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class CaseBranchTests(SharedSidecar sidecar)
{
    private sealed record Item(int Id, string Code, int Divisor, int Signed);

    private static readonly Item[] Items =
    [
        new(1, "42", 2, -3),
        new(2, "X", 0, 4),
        new(3, "7", 5, -5),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ExecutionEngine> Engines => new() { ExecutionEngine.Vectorised, ExecutionEngine.Reference };

    /// <summary>A cast in a branch is not evaluated for a row whose condition chose another.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_guarded_cast_raises_for_no_row_that_does_not_take_it(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine,
            "SELECT Id, CASE WHEN Code <> 'X' THEN CAST(Code AS DOUBLE) ELSE -1 END FROM s.items ORDER BY Id");

        Assert.Equal([42.0, -1.0, 7.0], rows.Select(r => Convert.ToDouble(r[1], System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A division guarded by its divisor's condition divides no row by zero.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_guarded_division_divides_no_row_by_zero(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine, "SELECT Id, CASE WHEN Divisor <> 0 THEN 10 / Divisor END FROM s.items ORDER BY Id");

        Assert.Equal([5, null, 2], rows.Select(r => r[1] is null ? (int?)null : Convert.ToInt32(r[1], System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A branch a row does take still raises for it, naming that row's value.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_branch_a_row_takes_still_raises_for_it(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => RowsAsync(
            engine, "SELECT CASE WHEN Id = 2 THEN CAST(Code AS DOUBLE) ELSE 0 END FROM s.items"));

        Assert.Contains("X", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same expression in a branch and outside it answers in full outside, whatever the branch
    /// was evaluated for; and a <c>CASE</c> over rows a filter has already narrowed reads only those.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task An_expression_in_a_branch_and_outside_it_answers_in_full_outside(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine,
            "SELECT Id, ABS(Signed), CASE WHEN Signed < 0 THEN ABS(Signed) ELSE 0 END, ABS(Signed) + 1 "
            + "FROM s.items ORDER BY Id");
        Assert.Equal([3, 4, 5], rows.Select(r => Convert.ToInt32(r[1], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal([3, 0, 5], rows.Select(r => Convert.ToInt32(r[2], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal([4, 5, 6], rows.Select(r => Convert.ToInt32(r[3], System.Globalization.CultureInfo.InvariantCulture)));

        var narrowed = await RowsAsync(
            engine,
            "SELECT Id, CASE WHEN Divisor > 1 THEN 10 / (Divisor - 2) ELSE 0 END FROM s.items WHERE Id <> 1 ORDER BY Id");
        Assert.Equal([0, 3], narrowed.Select(r => Convert.ToInt32(r[1], System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A <c>CASE</c> in a filter guards its branches as it does in a projection.</summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_case_in_a_filter_guards_its_branches(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        var rows = await RowsAsync(
            engine,
            "SELECT Id FROM s.items WHERE CASE WHEN Code = 'X' THEN 0 ELSE CAST(Code AS INTEGER) END > 10");

        Assert.Equal([1], rows.Select(r => Convert.ToInt32(r[0], System.Globalization.CultureInfo.InvariantCulture)));
    }

    private async Task<ChalkEngine> EngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "case-branches",
            Sources = [new PocoSourceBuilder("s", "s").AddTable("items", Items, t => t.UniqueKey(x => x.Id)).Build()],
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
