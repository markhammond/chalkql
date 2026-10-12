using System.Globalization;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An <c>ASOF JOIN</c>'s <c>ON</c> with a condition on one input (D337, F169). Calcite refused any
/// <c>ON</c> that was not equalities between the inputs, so a host joined a filtered subquery
/// <c>ON TRUE</c>. A condition on the right input now filters the right input, and one on the left
/// input of an inner ASOF join filters the left: each answers exactly what the explicit filter does.
/// </summary>
public sealed class AsofConditionTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime T0 = new(2026, 10, 12, 12, 0, 0);

    private const string Select = "SELECT t.id, r.rate FROM trades t ";

    [Theory]
    [InlineData(
        "ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON r.currency = 'EUR'",
        "ASOF JOIN (SELECT * FROM fx_rates WHERE currency = 'EUR') r MATCH_CONDITION t.ts >= r.ts ON TRUE",
        "1|1.1 2|1.2 3|1.2")]
    [InlineData(
        "ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON t.currency = r.currency AND r.rate > 0.85",
        "ASOF JOIN (SELECT * FROM fx_rates WHERE rate > 0.85) r MATCH_CONDITION t.ts >= r.ts ON t.currency = r.currency",
        "1|1 2|1.2 3|0.9")]
    [InlineData(
        "ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON t.currency = r.currency AND t.qty > 5",
        "ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON t.currency = r.currency WHERE t.qty > 5",
        "2|1.2 3|0.8")]
    [InlineData(
        "LEFT ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON r.currency = 'EUR'",
        "LEFT ASOF JOIN (SELECT * FROM fx_rates WHERE currency = 'EUR') r MATCH_CONDITION t.ts >= r.ts ON TRUE",
        "1|1.1 2|1.2 3|1.2")]
    [InlineData(
        "LEFT ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON r.currency = 'GBP'",
        "LEFT ASOF JOIN (SELECT * FROM fx_rates WHERE currency = 'GBP') r MATCH_CONDITION t.ts >= r.ts ON TRUE",
        "1| 2| 3|")]
    public async Task A_condition_on_one_input_answers_what_the_explicit_filter_does(
        string join, string explicitly, string expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var rows = await RowsAsync(engine, Select + join + " ORDER BY t.id");
        var explicitRows = await RowsAsync(engine, Select + explicitly + " ORDER BY t.id");

        Assert.Equal(expected, rows);
        Assert.Equal(explicitRows, rows);
    }

    [Fact]
    public async Task A_condition_on_the_left_input_of_a_left_asof_join_is_refused_saying_what_to_do()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var refused = await Assert.ThrowsAsync<PlanningException>(async () => await engine.PrepareAsync(
            Select + "LEFT ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON t.qty > 5"));

        Assert.Equal(ChalkErrorCodes.SqlValidation, refused.Code);
        Assert.Contains(
            "a left row that fails 't.qty > 5' must still appear, with NULLs. Move the condition to WHERE",
            refused.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_condition_that_does_not_qualify_its_column_is_refused_saying_what_to_do()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var refused = await Assert.ThrowsAsync<PlanningException>(async () => await engine.PrepareAsync(
            Select + "ASOF JOIN fx_rates r MATCH_CONDITION t.ts >= r.ts ON rate > 0"));

        Assert.Equal(ChalkErrorCodes.SqlValidation, refused.Code);
        Assert.Contains("'rate > 0' names a column without its input's alias", refused.Message, StringComparison.Ordinal);
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "asof-conditions",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable(
                        "trades",
                        new Trade[]
                        {
                            new(1, "USD", T0.AddSeconds(5), 3),
                            new(2, "EUR", T0.AddSeconds(15), 8),
                            new(3, "USD", T0.AddSeconds(25), 10),
                        })
                    .AddTable(
                        "fx_rates",
                        new FxRate[]
                        {
                            new("EUR", T0, 1.10),
                            new("USD", T0, 1.00),
                            new("EUR", T0.AddSeconds(10), 1.20),
                            new("USD", T0.AddSeconds(10), 0.90),
                            new("USD", T0.AddSeconds(20), 0.80),
                        })
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
        });

    /// <summary>Each row as <c>id|rate</c>, space-separated, a NULL rate empty.</summary>
    private static async Task<string> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<string>();
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            var ids = (Int32Array)batch.Column(0);
            var rates = (DoubleArray)batch.Column(1);
            for (var i = 0; i < batch.Length; i++)
            {
                var rate = rates.GetValue(i)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                rows.Add($"{ids.GetValue(i)}|{rate}");
            }

            batch.Dispose();
        }

        return string.Join(' ', rows);
    }

    public sealed record Trade(int Id, string Currency, DateTime Ts, int Qty);

    public sealed record FxRate(string Currency, DateTime Ts, double Rate);
}
