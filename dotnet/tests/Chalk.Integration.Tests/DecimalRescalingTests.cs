using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// DECIMAL arithmetic that keeps its integer digits (D338, design 63 §5, F183). A product of two
/// <c>DECIMAL(28,8)</c> values is typed <c>DECIMAL(38,6)</c>, as SQL Server types it, and a quotient
/// of that by a <c>DECIMAL(28,8)</c> is too. Each is computed exactly and rounded half away from zero,
/// as every DECIMAL result is. Before, Calcite kept every fractional digit, and the kernel, working in
/// <see cref="decimal"/>, refused: "DECIMAL scale 30 is beyond what System.Decimal can represent".
/// Both engines answer.
/// </summary>
public sealed class DecimalRescalingTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_notional_in_the_reporting_currency_keeps_its_integer_digits(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var values = await DecimalsAsync(
            engine, "SELECT qty * usd_price AS product, qty * usd_price / usd_rate AS notional FROM positions WHERE id = 1");

        // Exactly 121 932 555 416.8480998831564186, at six places; then divided by 1.08765432.
        Assert.Equal(121_932_555_416.848100m, values[0]);
        Assert.Equal(112_105_981_813.089383m, values[1]);
    }

    /// <summary>A product exactly halfway at the reduced scale rounds away from zero.</summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_halfway_product_rounds_away_from_zero(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var values = await DecimalsAsync(engine, "SELECT qty * usd_price AS product FROM positions WHERE id = 2");

        // 0.000001 × 0.5 is 0.0000005, at six places 0.000001.
        Assert.Equal(0.000001m, values[0]);
    }

    /// <summary>A product whose integer part needs more than 32 digits does not fit DECIMAL(38,6).</summary>
    [Fact]
    public async Task A_product_past_38_digits_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(ExecutionEngine.Vectorised);

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => DecimalsAsync(engine, "SELECT qty * usd_price AS product FROM positions WHERE id = 3"));

        Assert.Contains("does not fit in DECIMAL(38,6)", failure.Message, StringComparison.Ordinal);
    }

    private static readonly Position[] Positions =
    [
        new(1, 1_234_567.12345678m, 98_765.43210987m, 1.08765432m),
        new(2, 0.000001m, 0.5m, 1m),
        new(3, 99_999_999_999_999_999_999m, 99_999_999_999_999_999_999m, 1m),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "decimal-rescaling",
            Sources = [new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("positions", Positions).Build()],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    private static async Task<List<decimal>> DecimalsAsync(ChalkEngine engine, string sql)
    {
        var values = new List<decimal>();
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var c = 0; c < batch.ColumnCount; c++)
            {
                var column = (Decimal128Array)batch.Column(c);
                Assert.Equal(6, ((Apache.Arrow.Types.Decimal128Type)column.Data.DataType).Scale);
                values.Add(column.GetValue(0)!.Value);
            }

            batch.Dispose();
        }

        return values;
    }

    public sealed record Position(
        int Id,
        [property: ChalkColumn(Precision = 28, Scale = 8)] decimal Qty,
        [property: ChalkColumn(Precision = 28, Scale = 8)] decimal UsdPrice,
        [property: ChalkColumn(Precision = 28, Scale = 8)] decimal UsdRate);
}
