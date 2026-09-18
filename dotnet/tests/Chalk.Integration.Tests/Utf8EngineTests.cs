using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The <c>utf8_symbols</c> fixture through the engine (D145, D146,
/// <c>docs/design/24-zero-gc.md</c> §9): a <c>Utf8String</c> key filtered, matched with
/// <c>LIKE</c>, bound as a parameter, and the two Tier 1 functions written in <c>Utf8String</c>
/// compared against the reference executor running the same delegates.
/// </summary>
/// <remarks>
/// A fixture and a catalog of its own, so nothing here moves a recorded plan: this step is below
/// the plan, and the corpus catalog is untouched.
/// </remarks>
[Collection(SidecarCollection.Name)]
[Experimental("CHALK001")]
public sealed class Utf8EngineTests(SharedSidecar sidecar)
{
    private static Utf8Fixture Fixture => Utf8Fixture.Shared;

    [Fact]
    public async Task An_equality_filter_on_a_Utf8String_key_matches_by_bytes()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync(ExecutionEngine.Vectorised);
        Assert.Equal(
            ["BTCUSDT"],
            await TextAsync(engine, "SELECT symbol FROM utf8_symbols WHERE symbol = 'BTCUSDT'"));

        // Ordinal, so a case that differs is a different value.
        Assert.Empty(await TextAsync(
            engine, "SELECT symbol FROM utf8_symbols WHERE symbol = 'btcusdt'"));

        // And non-ASCII compares by bytes, which is what the column is collated in.
        Assert.Equal(
            ["ÉTOILEUSDT"],
            await TextAsync(engine, "SELECT symbol FROM utf8_symbols WHERE symbol = 'ÉTOILEUSDT'"));
        // A value outside Latin-1 is fine in the *data* and, since step 25 §0b, in a SQL *literal*
        // too: the planner's character set is UTF-8 rather than Calcite's ISO-8859-1 default, so
        // this no longer fails to plan (V46 was the defect, V47 the fix).
        Assert.Equal(["日経USDT"], await TextAsync(engine, "SELECT CAST('日経USDT' AS VARCHAR) AS s"));
        Assert.Equal(
            ["日経USDT"],
            await TextAsync(engine, "SELECT symbol FROM utf8_symbols WHERE symbol = '日経USDT'"));

        // A comparison, in bytes: 日 begins 0xE6 and É begins 0xC3, so only the CJK symbol is
        // above it. A culture comparison would not have agreed.
        Assert.Equal(
            ["日経USDT"],
            await TextAsync(
                engine, "SELECT symbol FROM utf8_symbols WHERE symbol > 'ÉTOILEUSDT'"));
    }

    [Fact]
    public async Task A_LIKE_over_a_Utf8String_column_matches_what_it_should()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync(ExecutionEngine.Vectorised);
        var prefixed = await TextAsync(
            engine, "SELECT symbol FROM utf8_symbols WHERE symbol LIKE 'A%' ORDER BY symbol");
        Assert.Equal(["ADAUSDT", "A_VERY_LONG_SYMBOL_NAMEUSDT", "AppleUSDT"], prefixed);

        // `_` is one *character*, not one byte: É is two bytes and still one wildcard.
        var single = await TextAsync(
            engine, "SELECT symbol FROM utf8_symbols WHERE symbol LIKE '_TOILEUSDT'");
        Assert.Equal(["ÉTOILEUSDT"], single);

        var infix = await TextAsync(
            engine, "SELECT label FROM utf8_symbols WHERE label LIKE '%ï%' ORDER BY label");
        Assert.Equal(["naïve"], infix);
    }

    [Fact]
    public async Task A_Utf8String_binds_as_a_parameter_like_a_string_does()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync(ExecutionEngine.Vectorised);
        const string Sql = "SELECT symbol FROM utf8_symbols WHERE symbol = @s";

        var viaBytes = await TextAsync(
            engine, Sql, new { s = Utf8String.FromString("日経USDT") });
        var viaString = await TextAsync(engine, Sql, new { s = "日経USDT" });

        Assert.Equal(["日経USDT"], viaBytes);
        Assert.Equal(viaString, viaBytes);
    }

    /// <summary>
    /// The two Tier 1 fixture functions, vectorised against the reference executor running the same
    /// delegate. What is compared is the whole answer, so a byte the lane loop got wrong shows up as
    /// a disagreement rather than as a plausible string.
    /// </summary>
    [Theory]
    [InlineData("SELECT upper_ascii(symbol) AS s FROM utf8_symbols ORDER BY symbol")]
    [InlineData("SELECT byte_length(symbol) AS n FROM utf8_symbols ORDER BY symbol")]
    [InlineData("SELECT upper_ascii(label) AS s FROM utf8_symbols WHERE label IS NOT NULL ORDER BY label")]
    [InlineData("SELECT symbol, byte_length(symbol) AS n, upper_ascii(symbol) AS u "
        + "FROM utf8_symbols WHERE byte_length(symbol) > 10 ORDER BY symbol")]
    public async Task A_Utf8String_Tier_1_function_agrees_with_the_reference_executor(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var vectorised = await CreateAsync(ExecutionEngine.Vectorised);
        await using var reference = await CreateAsync(ExecutionEngine.Reference);

        var actual = await BatchesAsync(vectorised, sql);
        var expected = await BatchesAsync(reference, sql);
        try
        {
            ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered);
        }
        finally
        {
            foreach (var batch in actual.Concat(expected))
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>
    /// <c>upper_ascii</c> leaves every multi-byte sequence alone, which is the whole difference
    /// between an ASCII operation over bytes and a culture one over characters.
    /// </summary>
    [Fact]
    public async Task upper_ascii_uppercases_ASCII_and_nothing_else()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync(ExecutionEngine.Vectorised);
        var upper = await TextAsync(
            engine,
            "SELECT upper_ascii(symbol) AS s FROM utf8_symbols "
            + "WHERE symbol IN ('naïveUSDT', 'MünchenUSDT') OR symbol > 'ÉTOILEUSDT' "
            + "ORDER BY symbol");

        // ü, ï and 日経 are untouched; every ASCII letter beside them is upper-cased.
        Assert.Equal(["MüNCHENUSDT", "NAïVEUSDT", "日経USDT"], upper);
    }

    private async Task<ChalkEngine> CreateAsync(ExecutionEngine execution) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = Utf8Fixture.ContextId,
            Functions = Utf8Fixture.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.Sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = execution, BatchSize = 512 },
        });

    private static async Task<List<string?>> TextAsync(
        ChalkEngine engine, string sql, object? parameters = null)
    {
        var values = new List<string?>();
        var batches = await BatchesAsync(engine, sql, parameters);
        try
        {
            foreach (var batch in batches)
            {
                var column = batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    // GetUtf8, so the egress helper of §5 is what the tests read through.
                    values.Add(column.IsNull(i) ? null : column.GetUtf8String(i));
                }
            }
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }

        return values;
    }

    private static async Task<List<RecordBatch>> BatchesAsync(
        ChalkEngine engine, string sql, object? parameters = null)
    {
        var batches = new List<RecordBatch>();
        var stream = engine.QueryAsync(
            sql,
            (IReadOnlyList<object?>?)null,
            arena: null,
            TestContext.Current.CancellationToken);
        if (parameters is not null)
        {
            stream = engine.QueryAsync(
                sql, parameters, arena: null, TestContext.Current.CancellationToken);
        }

        await foreach (var batch in stream)
        {
            batches.Add(batch);
        }

        return batches;
    }
}
