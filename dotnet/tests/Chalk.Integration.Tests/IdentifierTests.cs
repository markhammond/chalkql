using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The identifier contract the README states: unquoted identifiers keep their case and match
/// case-insensitively, so <c>SELECT Symbol FROM Bars</c> finds the <c>symbol</c> column and names the
/// output field <c>Symbol</c>.
/// </summary>
/// <remarks>
/// The naming half of that is not free. Calcite's <c>ProjectRemoveRule</c> compares a projection's
/// indexes and types but not its field names, so a rename-only <c>Project</c> is trivial to it and is
/// optimised away — leaving a physical plan whose root carries the table's spelling rather than the
/// query's. <c>RelToIr</c> puts the names back, and has to do it without breaking I-IR-4, which says a
/// <c>Filter</c>'s output row equals its input row down to the names.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class IdentifierTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 10);

    [Theory]
    [InlineData("SELECT Symbol, Ts FROM Bars", new[] { "Symbol", "Ts" })]
    [InlineData("SELECT SYMBOL, TS FROM BARS WHERE SYMBOL = 'BTCUSDT'", new[] { "SYMBOL", "TS" })]
    [InlineData("SELECT symbol, ts FROM bars", new[] { "symbol", "ts" })]
    [InlineData("SELECT Symbol AS s FROM bars WHERE Volume > 0", new[] { "s" })]
    public async Task An_unquoted_identifier_keeps_its_case_in_the_output_schema(
        string sql, string[] expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(sql);

        Assert.Equal(expected, prepared.OutputSchema.FieldsList.Select(f => f.Name));
        Assert.Equal(expected, prepared.Plan.OutputType.Fields.Select(f => f.Name));

        // PrepareAsync validates, so reaching here already proves the plan is well formed; asserting
        // it again says which invariant the renaming is most likely to break.
        PlanValidator.Validate(prepared.Plan);
    }

    /// <summary>
    /// The rename survives execution: a batch's schema is the prepared schema, not the table's.
    /// </summary>
    [Fact]
    public async Task A_renamed_root_still_executes()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync("SELECT Symbol, Ts FROM Bars WHERE Volume >= 0");

        var rows = 0;
        await using var execution = await engine.ExecuteAsync(prepared);
        await foreach (var batch in execution.Batches)
        {
            Assert.Equal(["Symbol", "Ts"], batch.Schema.FieldsList.Select(f => f.Name));
            rows += batch.Length;
            batch.Dispose();
        }

        Assert.Equal(Fixture.Bars.Count, rows);
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
        });
}
