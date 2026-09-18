using System.Text;
using Apache.Arrow;
using Chalk.Client;
using Chalk.TestKit;
using Array = System.Array;

namespace Chalk.Integration.Tests;

/// <summary>
/// The parameter styles end to end (D27) and list expansion (D29): corpus queries 31–35, executed
/// with real values against a real planner, because the interesting part is that the planner only
/// ever sees <c>?</c> and still infers the right types.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ParameterTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 1_440);

    [Fact]
    public async Task Named_parameters_reach_the_planner_as_question_marks()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var query = Corpus("31_params_named");
        var prepared = await engine.PrepareAsync(query.Sql);

        Assert.Equal(ParameterStyle.Named, prepared.ParameterStyle);
        Assert.Equal(["symbol", "from", "to"], prepared.Parameters.Select(p => p.Name));
        Assert.DoesNotContain('@', prepared.PlannerSql);
        Assert.Equal(3, prepared.ParameterTypes.Count);

        await using var execution = await engine.ExecuteAsync(
            prepared,
            new { symbol = "BTCUSDT", from = new DateTime(2026, 1, 1), to = new DateTime(2026, 1, 1, 0, 10, 0) });

        Assert.Equal(10, await CountAsync(execution));
    }

    [Fact]
    public async Task An_ordinal_parameter_that_recurs_is_bound_once()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Corpus("32_params_ordinal_reuse").Sql);

        Assert.Equal(ParameterStyle.Ordinal, prepared.ParameterStyle);
        Assert.Equal(3, prepared.Parameters.Count);
        Assert.Equal(2, prepared.Parameters[0].Occurrences.Count);
        Assert.Equal(4, prepared.ParameterTypes.Count);

        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 1, 1, 0, 5, 0);
        await using var execution = await engine.ExecuteAsync(prepared, [from, "ETHUSDT", to]);

        var ethusdt = "ETHUSDT"u8.ToArray();
            
        // $1 is bound once and read in two places, so the count has to satisfy both occurrences.
        // `AND` binds tighter than `OR`, and the fixture leaves some trade_count NULL, so this is
        // not simply the five minutes of the window.
        var expected = Fixture.Bars.Count(b =>
            b.Ts >= from && b.Symbol == ethusdt && (b.TradeCount is null || (b.Ts < to && b.Ts >= from)));
        Assert.Equal(expected, await CountAsync(execution));
    }

    [Fact]
    public async Task A_list_parameter_expands_into_the_in_clause()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Corpus("33_params_list_in").Sql);

        Assert.True(prepared.Parameters.Single(p => p.Name == "symbols").AcceptsList);
        Assert.False(prepared.Parameters.Single(p => p.Name == "before").AcceptsList);

        // Prepared in the one-element shape, so element types surface here (§7.4).
        Assert.Contains("IN (?)", prepared.PlannerSql, StringComparison.Ordinal);

        await using var execution = await engine.ExecuteAsync(
            prepared,
            new { symbols = new[] { "BTCUSDT", "ETHUSDT" }, before = new DateTime(2026, 1, 1, 0, 3, 0) });

        // Three minutes × two symbols.
        Assert.Equal(6, await CountAsync(execution));
    }

    [Fact]
    public async Task The_same_prepared_query_serves_several_list_lengths()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Corpus("33_params_list_in").Sql);
        var before = new DateTime(2026, 1, 1, 0, 3, 0);

        foreach (var (symbols, expected) in new (string[] Symbols, int Rows)[]
                 {
                     (["BTCUSDT"], 3),
                     (["BTCUSDT", "ETHUSDT"], 6),
                     (["BTCUSDT", "ETHUSDT", "SOLUSDT"], 9),
                     (["BTCUSDT"], 3),
                 })
        {
            await using var execution = await engine.ExecuteAsync(prepared, new { symbols, before });

            Assert.Equal(expected, await CountAsync(execution));
        }
    }

    /// <summary>An empty <c>IN</c> list matches nothing, by SQL's three-valued logic (§7.4).</summary>
    [Fact]
    public async Task An_empty_in_list_returns_no_rows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Corpus("34_params_list_empty_in").Sql);

        await using var execution = await engine.ExecuteAsync(
            prepared, new { symbols = Array.Empty<string>() });

        Assert.Equal(0L, await SingleCountAsync(execution));
    }

    /// <summary>And an empty <c>NOT IN</c> list matches everything.</summary>
    [Fact]
    public async Task An_empty_not_in_list_returns_every_row()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Corpus("35_params_list_empty_not_in").Sql);

        await using var execution = await engine.ExecuteAsync(
            prepared, new { symbols = Array.Empty<string>() });

        Assert.Equal(Fixture.Bars.Count, await SingleCountAsync(execution));
    }

    [Fact]
    public async Task A_missing_or_extra_binding_is_reported_before_execution()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(Corpus("31_params_named").Sql);

        var missing = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.ExecuteAsync(prepared, new { symbol = "BTCUSDT" }).AsTask());
        Assert.Contains("@from", missing.Message, StringComparison.Ordinal);

        var extra = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.ExecuteAsync(
                prepared,
                new
                {
                    symbol = "BTCUSDT",
                    from = new DateTime(2026, 1, 1),
                    to = new DateTime(2026, 1, 2),
                    nope = 1,
                }).AsTask());
        Assert.Contains("@nope", extra.Message, StringComparison.Ordinal);
    }

    private static CorpusQuery Corpus(string name) =>
        CorpusQueries.Load().Single(q => q.Name == name);

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });

    private static async Task<int> CountAsync(QueryExecution execution)
    {
        var rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    private static async Task<long> SingleCountAsync(QueryExecution execution)
    {
        long? value = null;
        await foreach (var batch in execution.Batches)
        {
            Assert.Equal(1, batch.Length);
            value = ((Int64Array)batch.Column(0)).GetValue(0);
            batch.Dispose();
        }

        Assert.NotNull(value);
        return value!.Value;
    }
}
