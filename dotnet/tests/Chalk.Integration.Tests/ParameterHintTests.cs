using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// D284 — what a host says it expects a parameter to be worth, and what becomes of it.
/// </summary>
/// <remarks>
/// The resolution half is a pure function over the statement's own parameters and needs no planner.
/// The rest is end to end, because the claim being made is about a plan and about the rows that
/// come back from it.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ParameterHintTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 1_440);

    private const string ByOrdinal = "SELECT symbol, ts FROM bars WHERE symbol = ? AND ts >= ?";
    private const string ByName = "SELECT symbol, ts FROM bars WHERE symbol = @symbol AND ts >= @from";

    // ---- resolution ----

    [Fact]
    public void A_list_hints_by_ordinal_and_is_sparse()
    {
        var hints = Resolve(ByOrdinal, new object?[] { "BTCUSDT", null });

        Assert.Equal([0], hints.Select(h => h.Ordinal));
        Assert.Equal("BTCUSDT", hints[0].Value!.Literal.StringValue);
    }

    [Fact]
    public void A_dictionary_hints_by_name()
    {
        var hints = Resolve(
            ByName, new Dictionary<string, object?> { ["symbol"] = "BTCUSDT" });

        Assert.Equal([0], hints.Select(h => h.Ordinal));
        Assert.Equal("BTCUSDT", hints[0].Value!.Literal.StringValue);
    }

    [Fact]
    public void A_poco_hints_by_property_name()
    {
        var hints = Resolve(ByName, new { from = new DateTime(2026, 1, 2) });

        Assert.Equal([1], hints.Select(h => h.Ordinal));
    }

    /// <summary>
    /// A named parameter that occurs twice is one hint and two placeholders, and the planner numbers
    /// parameters by the placeholders it sees.
    /// </summary>
    [Fact]
    public void A_named_hint_is_expanded_to_every_occurrence()
    {
        var hints = Resolve(
            "SELECT symbol FROM bars WHERE ts >= @t AND ts < @t",
            new Dictionary<string, object?> { ["t"] = new DateTime(2026, 1, 2) });

        Assert.Equal([0, 1], hints.Select(h => h.Ordinal));
    }

    /// <summary>
    /// The one place the hint container and the execution binder read a value differently: a
    /// planning API needs three states where a binding API needs two.
    /// </summary>
    [Fact]
    public void Null_hints_nothing_and_DBNull_hints_sql_null()
    {
        Assert.Empty(Resolve(ByOrdinal, new object?[] { null, null }));

        var hints = Resolve(ByOrdinal, new object?[] { DBNull.Value, null });

        Assert.Equal([0], hints.Select(h => h.Ordinal));
        Assert.Null(hints[0].Value);
    }

    [Fact]
    public void Hinting_nothing_at_all_sends_nothing()
    {
        Assert.Empty(Resolve(ByOrdinal, null));
    }

    /// <summary>A misspelt hint must not quietly become no hint.</summary>
    [Fact]
    public void An_unknown_hint_name_is_refused_by_name()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => Resolve(ByName, new { symbl = "BTCUSDT" }));

        Assert.Contains("@symbl", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the statement has no such parameter", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_hints_than_parameters_is_refused()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => Resolve(ByOrdinal, new object?[] { "BTCUSDT", null, 3 }));

        Assert.Contains("3 parameter value hints were given", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A list parameter is planned per list length, so the plan's shape follows the length and not
    /// any one value in it. There is nothing a hint could usefully say.
    /// </summary>
    [Fact]
    public void A_list_valued_hint_is_refused_by_name()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => Resolve(
                "SELECT symbol FROM bars WHERE symbol IN @symbols",
                new Dictionary<string, object?> { ["symbols"] = new[] { "BTCUSDT", "ETHUSDT" } }));

        Assert.Contains("@symbols", failure.Message, StringComparison.Ordinal);
        Assert.Contains("a list was given as the value hint", failure.Message, StringComparison.Ordinal);
    }

    // ---- end to end ----

    [Fact]
    public async Task A_hinted_prepare_lists_its_hinted_ordinals_and_keeps_nothing_else()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(
            "SELECT symbol, ts FROM bars WHERE volume >= @floor LIMIT @take",
            new { floor = 9000L, take = 1 });

        Assert.Equal([0, 1], prepared.HintedParameters);
        Assert.DoesNotContain("9000", prepared.PlanText ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Invariant 4: execution is planner-free. The prepared plan is the plan, whatever is bound —
    /// so values that contradict the hints run it unchanged and answer the same rows the unhinted
    /// plan answers for those values.
    /// </summary>
    [Fact]
    public async Task Values_that_contradict_the_hints_run_the_prepared_plan_and_answer_the_same_rows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        const string sql = "SELECT symbol, ts FROM bars WHERE volume >= @floor ORDER BY ts, symbol LIMIT @take";

        var hinted = await engine.PrepareAsync(sql, new { floor = 9000L, take = 1 });
        var unhinted = await engine.PrepareAsync(sql);

        // Values the hints are wrong about in both directions.
        var values = new { floor = 1000L, take = 12 };

        var fromHinted = await RowsAsync(engine, hinted, values);
        var fromUnhinted = await RowsAsync(engine, unhinted, values);

        Assert.NotEmpty(fromHinted);
        Assert.Equal(fromUnhinted.Count, fromHinted.Count);
        for (var i = 0; i < fromHinted.Count; i++)
        {
            Assert.Equal(fromUnhinted[i], fromHinted[i]);
        }
    }

    /// <summary>
    /// Invariant 2: hints change cost, never rows. The corpus pair is one statement recorded twice —
    /// once with hints and once without — and executed with the same bound values; the plans differ
    /// by the row goal the hints bought, and the rows do not differ at all.
    /// </summary>
    [Fact]
    public async Task The_corpus_pair_plans_differently_and_answers_the_same_rows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var unhinted = CorpusQueries.LoadM2().Single(q => q.Name == "15_parameterised_bound_unhinted");
        var hinted = CorpusQueries.LoadM2().Single(q => q.Name == "16_parameterised_bound_hinted");

        Assert.Equal(unhinted.Sql, hinted.Sql);
        Assert.Empty(CorpusQueries.ResolvedHints(unhinted));
        Assert.NotEmpty(CorpusQueries.ResolvedHints(hinted));

        var withoutHints = await engine.PrepareAsync(unhinted.Sql, unhinted.PrepareOptions());
        var withHints = await engine.PrepareAsync(hinted.Sql, hinted.PrepareOptions());

        Assert.Empty(withoutHints.HintedParameters);
        Assert.Equal([0, 1], withHints.HintedParameters);

        var (plain, _) = await DifferentialRunner.RunAsync(engine, withoutHints, unhinted);
        var (goaled, _) = await DifferentialRunner.RunAsync(engine, withHints, hinted);
        try
        {
            Assert.Equal(BatchReader.ToStorageRows(plain), BatchReader.ToStorageRows(goaled));
        }
        finally
        {
            foreach (var batch in plain.Concat(goaled))
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>Invariant 3: a hint's value is not in the plan, and not in a refusal either.</summary>
    [Fact]
    public async Task A_hints_value_reaches_no_plan_text_and_no_refusal()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        const long distinctive = 8675309L;

        var prepared = await engine.PrepareAsync(
            "SELECT symbol, ts FROM bars WHERE volume >= @floor",
            new { floor = distinctive },
            new PrepareOptions { IncludePlanText = true });

        Assert.DoesNotContain("8675309", prepared.PlanText ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal([0], prepared.HintedParameters);

        // And the sidecar's own log, which is the other place a value could have been written down.
        Assert.DoesNotContain(
            "8675309", string.Join('\n', sidecar.Sidecar.Diagnostics()), StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(
                "SELECT symbol, ts FROM bars WHERE volume >= @floor",
                new { floor = "8675309" }).AsTask());

        Assert.Contains("the value hint for parameter 0", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("8675309", failure.Message, StringComparison.Ordinal);
    }

    // ---- support ----

    private static IReadOnlyList<ParameterValueHint> Resolve(string sql, object? container)
    {
        var rewriter = ParameterRewriter.Parse(sql);
        var rendered = rewriter.Render(rewriter.PrepareShape());
        return ParameterBinder.ResolveHints(rewriter.Parameters, rendered.Slots, container);
    }

    /// <summary>Every row of an execution, as storage values, which is what the assertions compare.</summary>
    private static async Task<List<object?[]>> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, object values)
    {
        var batches = new List<Apache.Arrow.RecordBatch>();
        await using var execution = await engine.ExecuteAsync(prepared, values);
        await foreach (var batch in execution.Batches)
        {
            batches.Add(batch);
        }

        var rows = BatchReader.ToStorageRows(batches);
        foreach (var batch in batches)
        {
            batch.Dispose();
        }

        return rows;
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
