using System.Diagnostics.CodeAnalysis;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The claims of step 22 that are properties of an <em>execution</em> rather than of a SQL text's
/// result, so they are tests rather than corpus queries — the treatment ADR 0020 gave the same shape
/// of claim in step 21.
/// </summary>
[Collection(SidecarCollection.Name)]
[Experimental("CHALK001")]
public sealed class UserFunctionEngineTests(SharedSidecar sidecar)
{
    private sealed record Reading(long Id, double Value);

    /// <summary>
    /// §5 corpus 08. A VOLATILE call is evaluated per lane and never de-duplicated, so three rows
    /// carry three different values — which is exactly what a differential comparison cannot assert,
    /// because two executions of a volatile function do not agree with each other either.
    /// </summary>
    [Fact]
    public async Task A_volatile_call_answers_once_per_row()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        CorpusFunctions.ResetSequence();
        await using var engine = await CreateAsync();
        var values = await ValuesAsync(engine, "SELECT next_seq() AS n FROM bars_small LIMIT 3");

        Assert.Equal(3, values.Count);
        Assert.Equal(3, values.Distinct().Count());
    }

    /// <summary>
    /// §5 corpus 09's claim, stated as a fact about the execution: a STABLE call of constants is
    /// evaluated once and broadcast, so every row carries the same value. The corpus query asserts
    /// the answer; this asserts that it was computed once.
    /// </summary>
    [Fact]
    public async Task A_stable_call_answers_once_per_execution()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync();
        var values = await ValuesAsync(engine, "SELECT as_of() AS a FROM bars_small LIMIT 100");

        Assert.Equal(100, values.Count);
        Assert.Single(values.Distinct());
        Assert.Equal(CorpusFunctions.AsOfTicks, values[0]);
    }

    /// <summary>
    /// §5's negative: engine creation fails when a client-bodied function in the catalog has no
    /// registered implementation, before any query runs.
    /// </summary>
    [Fact]
    public async Task Engine_creation_fails_when_a_client_body_has_no_implementation()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("t", new[] { new Reading(1, 1.0) })
            .AddFunction("missing", f => f.Scalar<double, double>("x").Strict().Client())
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "no-implementation",
                Sources = [source],
                Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
            }).AsTask());

        Assert.Contains("'missing'", error.Message, StringComparison.Ordinal);
        Assert.Contains("ChalkEngineOptions.Functions", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The same, for a delegate whose CLR types disagree with the declaration.</summary>
    [Fact]
    public async Task Engine_creation_fails_when_a_delegate_has_the_wrong_clr_types()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("t", new[] { new Reading(1, 1.0) })
            .AddFunction("wrong", f => f.Scalar<double, double>("x").Strict().Client())
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "wrong-types",
                Sources = [source],
                Functions = registry => registry.AddScalar<long, long>("wrong", static v => v),
                Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
            }).AsTask());

        Assert.Contains("FP64", error.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A name a built-in already has is refused at registration, not shadowed (D77).</summary>
    [Fact]
    public async Task A_clash_with_a_built_in_is_a_registration_error()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem")
            .AddTable("t", new[] { new Reading(1, 1.0) })
            .AddFunction("upper", f => f.Scalar<string, string>("s").Strict().Client())
            .Build();

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "clash",
                Sources = [source],
                Functions = registry => registry.AddScalar<string, string>("upper", static s => s),
                Planner = sidecar.CreatePlanner(),
            }).AsTask());

        Assert.Contains("upper", error.Message, StringComparison.Ordinal);
    }

    private async ValueTask<ChalkEngine> CreateAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = CorpusFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
        });

    private static async Task<List<long>> ValuesAsync(ChalkEngine engine, string sql)
    {
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var values = new List<long>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                values.AddRange(
                    ResultComparer.Rows([batch]).Select(row => Convert.ToInt64(row[0])));
            }
        }

        return values;
    }
}
