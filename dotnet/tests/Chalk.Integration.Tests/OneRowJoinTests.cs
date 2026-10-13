using System.Globalization;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A join with a one-row constant table, planned as a projection of its other side (D348), answers
/// exactly what the join did. A cross or inner join gives every row it keeps the table's values; a
/// left or right join keeps every row of the other side, with the values where the condition holds
/// and NULL where it does not; an <c>EXISTS</c> of one row answers it once however many rows match;
/// an as-of join whose left side is one row still matches only the closest row; and a one-row table
/// keying an as-of join keys it as its constant would.
/// </summary>
public sealed class OneRowJoinTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime T0 = new(2026, 10, 12, 12, 0, 0);

    [Theory]
    [InlineData(
        "a cross join",
        "WITH reporting AS (SELECT CAST('EUR' AS VARCHAR(32)) AS currency) SELECT t.id, x.currency FROM trades t CROSS JOIN reporting x ORDER BY t.id",
        "1|EUR 2|EUR 3|EUR")]
    [InlineData(
        "an inner join",
        "SELECT t.id, x.flag FROM trades t JOIN (VALUES ('USD', 7)) AS x (currency, flag) ON t.currency = x.currency ORDER BY t.id",
        "1|7 3|7")]
    [InlineData(
        "a left join",
        "SELECT t.id, x.flag FROM trades t LEFT JOIN (VALUES ('USD', 7)) AS x (currency, flag) ON t.currency = x.currency ORDER BY t.id",
        "1|7 2| 3|7")]
    [InlineData(
        "a right join",
        "SELECT t.id, x.flag FROM (VALUES ('EUR', 5)) AS x (currency, flag) RIGHT JOIN trades t ON t.currency = x.currency ORDER BY t.id",
        "1| 2|5 3|")]
    [InlineData(
        "an EXISTS of one row, which three rates match",
        "SELECT v.currency, v.n FROM (VALUES ('USD', 1)) AS v (currency, n) WHERE EXISTS (SELECT 1 FROM fx_rates r WHERE r.currency = v.currency)",
        "USD|1")]
    [InlineData(
        "an as-of join of one row, which two rates precede",
        "SELECT s.currency, r.rate FROM (SELECT CAST('USD' AS VARCHAR(8)) AS currency, TIMESTAMP '2026-10-12 12:00:15' AS ts) s ASOF JOIN fx_rates r MATCH_CONDITION r.ts <= s.ts ON r.currency = s.currency",
        "USD|0.9")]
    [InlineData(
        "a one-row table keying an as-of join",
        "WITH reporting AS (SELECT CAST('EUR' AS VARCHAR(32)) AS currency) SELECT t.id, r.rate FROM trades t CROSS JOIN reporting x ASOF JOIN fx_rates r MATCH_CONDITION r.ts <= t.ts ON r.currency = x.currency ORDER BY t.id",
        "1|1.1 2|1.2 3|1.2")]
    public async Task A_join_with_one_row_answers_what_the_join_did(string form, string sql, string expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        Assert.Equal(expected, await RowsAsync(engine, sql));
        Assert.NotEmpty(form);
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "one-row-joins",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable(
                        "trades",
                        new Trade[]
                        {
                            new(1, "USD", T0.AddSeconds(5)),
                            new(2, "EUR", T0.AddSeconds(15)),
                            new(3, "USD", T0.AddSeconds(25)),
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

    /// <summary>Each row's columns joined by <c>|</c>, rows space-separated, a NULL empty.</summary>
    private static async Task<string> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<string>();
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add(string.Join('|', Enumerable.Range(0, batch.ColumnCount).Select(c =>
                        Convert.ToString(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i), CultureInfo.InvariantCulture))));
                }
            }
        }

        return string.Join(' ', rows);
    }

    public sealed record Trade(int Id, string Currency, DateTime Ts);

    public sealed record FxRate(string Currency, DateTime Ts, double Rate);
}
