using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A type declared in <see cref="PrepareOptions.ParameterTypes"/> holds when the statement is checked
/// (D337, F171). Before, the planner used it only to key its cache: Calcite typed a parameter from
/// what was around it, so <c>ts &gt;= ? - INTERVAL '60' SECOND</c> was refused whatever was declared,
/// and a host wrote <c>CAST(? AS TIMESTAMP(9))</c> instead.
/// </summary>
public sealed class DeclaredParameterTypeTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private const string Window = "SELECT id FROM trades WHERE ts >= ? - INTERVAL '60' SECOND ORDER BY id";

    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0);

    [Fact]
    public async Task A_declared_type_holds_where_inference_would_refuse_the_statement()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var inferred = await Assert.ThrowsAsync<PlanningException>(
            async () => await engine.PrepareAsync(Window));
        var prepared = await engine.PrepareAsync(
            Window, new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9)] });

        Assert.Equal(ChalkErrorCodes.SqlValidation, inferred.Code);
        Assert.Equal([ChalkType.Timestamp(9)], prepared.ParameterTypes);
        Assert.Equal([0L, 1L, 2L], await IdsAsync(engine, prepared, Now));
    }

    /// <summary>The declared nullability holds too: a parameter declared NOT NULL refuses NULL as it is bound.</summary>
    [Fact]
    public async Task A_declared_nullability_holds_when_the_value_is_bound()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();
        var notNull = await engine.PrepareAsync(
            Window, new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9)] });
        var nullable = await engine.PrepareAsync(
            Window, new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9, nullable: true)] });

        var refused = await Assert.ThrowsAsync<ParameterBindingException>(
            () => IdsAsync(engine, notNull, null));

        Assert.Equal(ChalkErrorCodes.ParameterBinding, refused.Code);
        Assert.Empty(await IdsAsync(engine, nullable, null));
    }

    [Fact]
    public async Task A_declared_type_that_does_not_fit_where_the_parameter_stands_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var refused = await Assert.ThrowsAsync<PlanningException>(async () => await engine.PrepareAsync(
            Window, new PrepareOptions { ParameterTypes = [ChalkType.Bool()] }));

        Assert.Equal(ChalkErrorCodes.SqlValidation, refused.Code);
        Assert.Contains("BOOLEAN", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declaration_without_one_type_per_parameter_is_refused_naming_both_counts()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var refused = await Assert.ThrowsAsync<PlanningException>(async () => await engine.PrepareAsync(
            Window, new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9), ChalkType.Int64()] }));

        Assert.Equal(ChalkErrorCodes.ParameterBinding, refused.Code);
        Assert.Contains(
            "The statement has 1 parameter, and 2 types were declared for them.",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A row count keeps the type it has always had, and is declared an integer or refused.</summary>
    [Fact]
    public async Task A_row_count_is_declared_an_integer()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();
        const string Top = "SELECT id FROM trades WHERE ts >= ? ORDER BY id LIMIT ?";

        var prepared = await engine.PrepareAsync(
            Top, new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9), ChalkType.Int64()] });
        var refused = await Assert.ThrowsAsync<PlanningException>(async () => await engine.PrepareAsync(
            Top, new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9), ChalkType.String()] }));

        Assert.Equal(ChalkType.Timestamp(9), prepared.ParameterTypes[0]);
        Assert.Equal(ChalkErrorCodes.ParameterBinding, refused.Code);
        Assert.Contains("Parameter 2 is a row count", refused.Message, StringComparison.Ordinal);
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "declared-parameters",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("trades", Enumerable.Range(0, 10).Select(i => new Trade(i, Now.AddSeconds(-25 * i))).ToArray())
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
        });

    private static async Task<List<long>> IdsAsync(ChalkEngine engine, PreparedQuery prepared, DateTime? now)
    {
        var ids = new List<long>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[now]);
        await foreach (var batch in execution.Batches)
        {
            var column = (Apache.Arrow.Int32Array)batch.Column(0);
            for (var i = 0; i < batch.Length; i++)
            {
                ids.Add(column.GetValue(i)!.Value);
            }

            batch.Dispose();
        }

        return ids;
    }

    public sealed record Trade(int Id, DateTime Ts);
}
