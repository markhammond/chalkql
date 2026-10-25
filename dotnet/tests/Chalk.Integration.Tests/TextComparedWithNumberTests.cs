using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// F278 through the client: text compared with a number that it cannot be read as is the statement's
/// mistake, refused as a validation error that names the text, the type and the column; before, it
/// failed in the planner as an internal assertion. Text that reads as the type is a value written in
/// quotes, and the statement answers as it would with the value itself. Text compared with a time is
/// read by ChalkQL's cast when the statement runs, offsets and all.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class TextComparedWithNumberTests(SharedSidecar sidecar)
{
    private sealed record Fill(long Seq, double Qty, int Version, DateTime FilledAt);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("qty = 'x'", "the text 'x' cannot be read as a DOUBLE, which qty is")]
    [InlineData("qty IN (1.5, 'x')", "the text 'x' cannot be read as a DOUBLE, which qty is")]
    [InlineData("version = '1.5'", "the text '1.5' cannot be read as an INTEGER, which version is")]
    public async Task Text_a_column_cannot_read_is_refused_as_the_statements_mistake(string condition, string said)
    {
        await using var engine = await CreateEngineAsync();

        var refused = await Assert.ThrowsAsync<PlanningException>(
            async () => await engine.PrepareAsync($"SELECT seq FROM fills WHERE {condition}", ct: Ct));

        Assert.Equal(ChalkErrorCodes.SqlValidation, refused.Code);
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_compared_with_a_time_is_read_by_the_cast_when_the_statement_runs()
    {
        await using var engine = await CreateEngineAsync();

        // An offset, which ChalkQL's cast reads and Calcite's literals do not; a TIMESTAMP keeps the
        // wall-clock time and leaves the offset, as TimestampOffsetTests holds it.
        Assert.Equal([1L, 2L], await SeqsAsync(engine, "SELECT seq FROM fills WHERE filled_at = '2026-10-25 09:00:00+02:00' ORDER BY seq"));
        var failed = await Assert.ThrowsAnyAsync<ChalkException>(
            async () => await SeqsAsync(engine, "SELECT seq FROM fills WHERE filled_at = 'tomorrow'"));
        Assert.Contains("tomorrow", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_that_reads_as_the_type_is_the_value_in_quotes()
    {
        await using var engine = await CreateEngineAsync();

        Assert.Equal([2L], await SeqsAsync(engine, "SELECT seq FROM fills WHERE qty = '1.5'"));
        Assert.Equal([1L], await SeqsAsync(engine, "SELECT seq FROM fills WHERE version = '3'"));
    }

    private static async Task<List<long>> SeqsAsync(ChalkEngine engine, string sql)
    {
        var seqs = new List<long>();
        await foreach (var batch in engine.QueryAsync(sql, new Dictionary<string, object?>(), ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    seqs.Add(Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i)));
                }
            }
        }

        return seqs;
    }

    private async Task<ChalkEngine> CreateEngineAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var at = new DateTime(2026, 10, 25, 9, 0, 0);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)[new Fill(1, 2.0, 3, at), new Fill(2, 1.5, 1, at)], t => t.Keyed(f => f.Seq))
            .Build();
        return await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
    }
}
