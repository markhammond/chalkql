using System.Text;
using System.Text.RegularExpressions;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// Redacted SQL end to end (D262, <c>docs/design/37-redacted-sql.md</c>, ADR 0043): what a host
/// gets, what it costs a host that never asks, and what correlates with what.
/// </summary>
/// <remarks>
/// Every salt here is fixed except where the point is that there is none, so nothing asserted
/// depends on randomness — which is §2's own rule for the tests and the fixtures.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed partial class RedactedSqlTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("a fixed salt");

    private const string Statement = "SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT' LIMIT 3";

    [GeneratedRegex(@"/\*REDACTED-([0-9a-f]{8}):([A-Z0-9_]+)\*/")]
    private static partial Regex Marker { get; }

    private static IReadOnlyList<string> Pseudonyms(string redacted) =>
        [.. Marker.Matches(redacted).Select(m => m.Groups[1].Value)];

    private async Task<ChalkEngine> CreateEngineAsync(RedactionOptions? redaction = null)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Redaction = redaction ?? new RedactionOptions(),
        });
    }

    private static RedactionOptions Salted(bool on = true) =>
        new() { IncludeRedactedSql = on, Salt = Salt };

    // ------------------------------------------------------------ opt-in

    /// <summary>
    /// The property the whole design turns on: an engine that does not ask gets nothing back and
    /// sends nothing to ask for it.
    /// </summary>
    [Fact]
    public async Task A_prepare_that_does_not_ask_carries_no_redaction_and_gets_none()
    {
        await using var engine = await CreateEngineAsync();
        var capture = new CapturingPlanner(sidecar.CreatePlanner());
        await using var watched = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = capture,
        });

        var prepared = await watched.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);

        Assert.Null(prepared.RedactedSql);
        Assert.NotEmpty(capture.Requests);
        Assert.All(capture.Requests, request => Assert.Null(request.Redaction));
    }

    /// <summary>And a prepare that asks gets one, with the salt the engine holds.</summary>
    [Fact]
    public async Task A_prepare_that_asks_carries_a_redaction_and_gets_one()
    {
        var capture = new CapturingPlanner(sidecar.CreatePlanner());
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = Fixture.Sources,
            Planner = capture,
            Redaction = Salted(),
        });

        var prepared = await engine.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(prepared.RedactedSql);
        Assert.DoesNotContain("BTCUSDT", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT 3 ROWS ONLY", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.Single(Pseudonyms(prepared.RedactedSql));
        Assert.All(capture.Requests, request => Assert.NotNull(request.Redaction));
    }

    /// <summary>One statement may ask even when the engine's default is off, and the other way.</summary>
    [Fact]
    public async Task A_statement_overrides_the_engines_default_both_ways()
    {
        await using var off = await CreateEngineAsync(new RedactionOptions { Salt = Salt });
        await using var on = await CreateEngineAsync(Salted());

        var asked = await off.PrepareAsync(
            Statement,
            new PrepareOptions { IncludeRedactedSql = true },
            TestContext.Current.CancellationToken);
        var declined = await on.PrepareAsync(
            Statement,
            new PrepareOptions { IncludeRedactedSql = false },
            TestContext.Current.CancellationToken);

        Assert.NotNull(asked.RedactedSql);
        Assert.Null(declined.RedactedSql);
    }

    /// <summary>A redaction is a rendering of the statement and never part of the plan.</summary>
    [Fact]
    public async Task Asking_for_a_redaction_does_not_change_the_plan()
    {
        await using var plain = await CreateEngineAsync();
        await using var redacting = await CreateEngineAsync(Salted());

        var left = await plain.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);
        var right = await redacting.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);

        Assert.Equal(left.PlanDigest, right.PlanDigest);
        Assert.Equal(left.Plan, right.Plan);
    }

    // ------------------------------------------------------------ what correlates

    /// <summary>A supplied salt is what makes two engines — two processes — agree.</summary>
    [Fact]
    public async Task Two_engines_under_one_salt_produce_the_same_text()
    {
        await using var first = await CreateEngineAsync(Salted());
        await using var second = await CreateEngineAsync(Salted());

        var left = await first.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);
        var right = await second.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);

        Assert.Equal(left.RedactedSql, right.RedactedSql);
    }

    /// <summary>
    /// And no salt is what stops them: the random per-engine salt is the conservative default, so
    /// two engines — or one restarted process — produce nothing anyone can line up.
    /// </summary>
    [Fact]
    public async Task Two_engines_with_no_salt_produce_pseudonyms_that_do_not_correlate()
    {
        await using var first = await CreateEngineAsync(new RedactionOptions { IncludeRedactedSql = true });
        await using var second = await CreateEngineAsync(new RedactionOptions { IncludeRedactedSql = true });

        var left = await first.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);
        var right = await second.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(left.RedactedSql);
        Assert.NotNull(right.RedactedSql);
        Assert.NotEqual(Pseudonyms(left.RedactedSql), Pseudonyms(right.RedactedSql));
        // The shape is the same either way — it is only the key that differs.
        Assert.Equal(Marker.Replace(left.RedactedSql, "?"), Marker.Replace(right.RedactedSql, "?"));
    }

    /// <summary>
    /// The structure is in the seed, so one value in two shapes does not correlate even under one
    /// salt, and one value in one shape does.
    /// </summary>
    [Fact]
    public async Task One_salt_correlates_a_shape_and_not_a_value_across_shapes()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var left = await engine.PrepareAsync(
            "SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'",
            ct: TestContext.Current.CancellationToken);
        var sameShape = await engine.PrepareAsync(
            "select symbol from bars where symbol = 'BTCUSDT'",
            ct: TestContext.Current.CancellationToken);
        var otherShape = await engine.PrepareAsync(
            "SELECT symbol FROM bars WHERE 'BTCUSDT' = symbol",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(Pseudonyms(left.RedactedSql!), Pseudonyms(sameShape.RedactedSql!));
        Assert.NotEqual(Pseudonyms(left.RedactedSql!), Pseudonyms(otherShape.RedactedSql!));
    }

    // ------------------------------------------------------------ RedactSqlAsync

    /// <summary>Text that was never prepared — and that does not parse, which is the whole point.</summary>
    [Fact]
    public async Task Redact_sql_serves_text_that_does_not_parse()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var redacted = await engine.RedactSqlAsync(
            "SELCT * FRM bars WHERE symbol = 'BTCUSDT' LIMIT 10",
            TestContext.Current.CancellationToken);

        Assert.False(redacted.Parsed);
        Assert.DoesNotContain("BTCUSDT", redacted.Sql, StringComparison.Ordinal);
        // Nothing is kept without a tree, the LIMIT included.
        Assert.DoesNotContain("10", redacted.Sql, StringComparison.Ordinal);
        Assert.Equal(64, redacted.StructuralHash.Length);
    }

    /// <summary>The same statement, whichever call produced it: one seed, one answer.</summary>
    [Fact]
    public async Task Redact_sql_and_a_prepare_agree_on_one_statement()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var prepared = await engine.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);
        var standalone = await engine.RedactSqlAsync(Statement, TestContext.Current.CancellationToken);

        Assert.True(standalone.Parsed);
        Assert.Equal(prepared.RedactedSql, standalone.Sql);
    }

    /// <summary>A Babel statement redacts under Babel's parser, D259's <c>::</c> cast included.</summary>
    [Fact]
    public async Task A_babel_statement_is_redacted_under_the_babel_conformance()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var prepared = await engine.PrepareAsync(
            "SELECT id::VARCHAR AS id_text FROM events WHERE kind = 'click'",
            new PrepareOptions
            {
                Conformance = SqlConformance.Babel,
                Libraries = [SqlLibrary.Postgresql],
                IncludeRedactedSql = true,
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared.RedactedSql);
        Assert.Contains(":: VARCHAR", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("click", prepared.RedactedSql, StringComparison.Ordinal);

        var standalone = await engine.RedactSqlAsync(
            "SELECT id::VARCHAR AS id_text FROM events WHERE kind = 'click'",
            SqlConformance.Babel,
            TestContext.Current.CancellationToken);
        Assert.True(standalone.Parsed);
        Assert.Equal(prepared.RedactedSql, standalone.Sql);
    }

    /// <summary>
    /// A host's parameters never reach the text at all: the planner sees the D27 rewrite, so the
    /// redaction is of a statement whose values are already placeholders — and a named one reads by
    /// its name again on the way back (D287).
    /// </summary>
    [Fact]
    public async Task A_parameterised_statement_redacts_the_rewritten_text()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var prepared = await engine.PrepareAsync(
            "SELECT symbol FROM bars WHERE symbol = @symbol",
            new PrepareOptions { IncludeRedactedSql = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared.RedactedSql);
        Assert.Contains("= @symbol", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("?", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.Empty(Pseudonyms(prepared.RedactedSql));
    }

    // ------------------------------------------------------------ the host's parameter names

    /// <summary>
    /// A parameter the host wrote as <c>@name</c> reads as <c>@name</c> in the redacted statement
    /// and in the redacted plan text, not as the positional placeholder the planner saw.
    /// </summary>
    [Fact]
    public async Task A_named_parameter_reads_by_its_name_in_the_redacted_text_and_the_plan_text()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var prepared = await engine.PrepareAsync(
            "SELECT symbol FROM bars WHERE symbol = @symbol AND ts >= @since",
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared.RedactedSql);
        Assert.Contains("= @symbol", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.Contains(">= @since", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("?", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.NotNull(prepared.PlanText);
        Assert.Contains("@symbol", prepared.PlanText, StringComparison.Ordinal);
        Assert.Contains("@since", prepared.PlanText, StringComparison.Ordinal);
        Assert.DoesNotContain("?0", prepared.PlanText, StringComparison.Ordinal);
        Assert.DoesNotContain("?1", prepared.PlanText, StringComparison.Ordinal);
    }

    /// <summary>The same for text that was never prepared: rewritten as a prepare would, names put back.</summary>
    [Fact]
    public async Task Redact_sql_puts_the_names_back_too()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var redacted = await engine.RedactSqlAsync(
            "SELECT symbol FROM bars WHERE symbol = @symbol AND ts >= 5",
            TestContext.Current.CancellationToken);

        Assert.True(redacted.Parsed);
        Assert.Contains("= @symbol", redacted.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("?", redacted.Sql, StringComparison.Ordinal);
        Assert.Single(Pseudonyms(redacted.Sql));
    }

    /// <summary>An ordinal or positional statement has no names to put back and keeps its <c>?</c>s.</summary>
    [Fact]
    public async Task An_ordinal_statement_keeps_its_positional_placeholders()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var prepared = await engine.PrepareAsync(
            "SELECT symbol FROM bars WHERE symbol = $1",
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared.RedactedSql);
        Assert.Contains("= ?", prepared.RedactedSql, StringComparison.Ordinal);
        Assert.NotNull(prepared.PlanText);
        Assert.Contains("?0", prepared.PlanText, StringComparison.Ordinal);
    }

    /// <summary>The names go back only into a redacted text; a plain plan text is the one it always was.</summary>
    [Fact]
    public async Task A_prepare_that_does_not_redact_keeps_the_plan_texts_placeholders()
    {
        await using var engine = await CreateEngineAsync();

        var prepared = await engine.PrepareAsync(
            "SELECT symbol FROM bars WHERE symbol = @symbol",
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.Null(prepared.RedactedSql);
        Assert.NotNull(prepared.PlanText);
        Assert.Contains("?0", prepared.PlanText, StringComparison.Ordinal);
        Assert.DoesNotContain("@symbol", prepared.PlanText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the context names

    /// <summary>
    /// A literal in a pushed query's text that is, in type and value, one of the request context's
    /// bound values is labelled with the name it was bound under — in the query text a source
    /// failure quotes, and never in the redacted statement, which names the reference rather than
    /// its value.
    /// </summary>
    [Fact]
    public async Task A_pushed_query_labels_a_literal_that_is_a_bound_value()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = [.. Fixture.Sources, BrokenSource()],
            Planner = sidecar.CreatePlanner(),
            Redaction = Salted(),
        });
        var context = new RequestContext().With("sym", "BTCUSDT");

        var prepared = await engine.PrepareAsync(
            "SELECT symbol, v FROM down.bars_down WHERE symbol = 'BTCUSDT'",
            context,
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(prepared.RedactedSql);
        Assert.DoesNotContain("@ctx.", prepared.RedactedSql, StringComparison.Ordinal);

        await using var execution = await engine.ExecuteAsync(
            prepared, (IReadOnlyList<object?>?)null, ct: TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<Chalk.ChalkException>(async () =>
        {
            await foreach (var batch in execution.Batches.WithCancellation(
                TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        var attributed = Find<Chalk.Sources.SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.DoesNotContain("BTCUSDT", attributed.Subject, StringComparison.Ordinal);
        Assert.Contains(":CHAR @ctx.sym*/", attributed.Subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// A context value folded into an entitled table's predicate is a literal in the plan, and its
    /// marker names the entry it came from.
    /// </summary>
    [Fact]
    public async Task A_folded_context_value_is_labelled_in_the_plan_text()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Redaction = Salted(),
        });

        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM members",
            TenancyFixture.U2,
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared.Query.PlanText);
        Assert.Contains(" @ctx.agent_orgs*/", prepared.Query.PlanText, StringComparison.Ordinal);
        Assert.NotNull(prepared.Query.RedactedSql);
        Assert.DoesNotContain("@ctx.", prepared.Query.RedactedSql, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the plan text

    /// <summary>
    /// The plan text is rendered with the same pseudonyms when the prepare asked for a redaction —
    /// so a host that logs the plan and the statement logs one pseudonym for one value.
    /// </summary>
    [Fact]
    public async Task The_plan_text_carries_the_same_pseudonyms_when_the_prepare_redacts()
    {
        await using var engine = await CreateEngineAsync(Salted());

        var redacted = await engine.PrepareAsync(
            Statement,
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(redacted.PlanText);
        Assert.DoesNotContain("BTCUSDT", redacted.PlanText, StringComparison.Ordinal);
        Assert.NotEmpty(Pseudonyms(redacted.PlanText));
        Assert.All(
            Pseudonyms(redacted.RedactedSql!),
            hex => Assert.Contains(hex, Pseudonyms(redacted.PlanText)));
        // LIMIT is a count of rows in a plan exactly as it is in a statement, and is kept.
        Assert.Contains("fetch=[3]", redacted.PlanText, StringComparison.Ordinal);
    }

    /// <summary>And the plan text of a prepare that did not ask is the one it always was.</summary>
    [Fact]
    public async Task The_plan_text_is_untouched_when_the_prepare_does_not_redact()
    {
        await using var engine = await CreateEngineAsync();

        var plain = await engine.PrepareAsync(
            Statement,
            new PrepareOptions { IncludePlanText = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(plain.PlanText);
        Assert.Contains("BTCUSDT", plain.PlanText, StringComparison.Ordinal);
        Assert.DoesNotContain("REDACTED", plain.PlanText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ a source failure

    /// <summary>
    /// The other half of "no log line is half safe" (D262): a source's failure names what it was
    /// running, and what it was running is the pushed SQL with this statement's literals folded into
    /// it. With the engine's redaction on, the quoted text carries pseudonyms.
    /// </summary>
    /// <remarks>
    /// The source is a real ADO registration pointed at a path that cannot be opened, so the failure
    /// comes from a driver rather than from a stub — the same shape
    /// <see cref="FederationFailureTests"/> uses for the unredacted case.
    /// </remarks>
    [Fact]
    public async Task A_source_failure_quotes_the_redacted_query_when_the_engine_redacts()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = [.. Fixture.Sources, BrokenSource()],
            Planner = sidecar.CreatePlanner(),
            Redaction = Salted(),
        });

        var prepared = await engine.PrepareAsync(
            "SELECT symbol, v FROM down.bars_down WHERE symbol = 'BTCUSDT'",
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(prepared.RedactedSql);

        await using var execution = await engine.ExecuteAsync(
            prepared, (IReadOnlyList<object?>?)null, ct: TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<Chalk.ChalkException>(async () =>
        {
            await foreach (var batch in execution.Batches.WithCancellation(
                TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        var attributed = Find<Chalk.Sources.SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.Equal("down", attributed.SourceId);
        Assert.DoesNotContain("BTCUSDT", attributed.Message, StringComparison.Ordinal);
        Assert.NotEmpty(Pseudonyms(attributed.Subject));
    }

    /// <summary>And an engine that did not ask sees the query exactly as it was pushed.</summary>
    [Fact]
    public async Task A_source_failure_quotes_the_query_as_pushed_when_the_engine_does_not_redact()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = [.. Fixture.Sources, BrokenSource()],
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine.PrepareAsync(
            "SELECT symbol, v FROM down.bars_down WHERE symbol = 'BTCUSDT'",
            ct: TestContext.Current.CancellationToken);

        await using var execution = await engine.ExecuteAsync(
            prepared, (IReadOnlyList<object?>?)null, ct: TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<Chalk.ChalkException>(async () =>
        {
            await foreach (var batch in execution.Batches.WithCancellation(
                TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        var attributed = Find<Chalk.Sources.SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.Contains("BTCUSDT", attributed.Subject, StringComparison.Ordinal);
    }

    /// <summary>A source that describes a schema and cannot be opened.</summary>
    private static Chalk.Sources.Ado.AdoSource BrokenSource()
    {
        var profile = DialectProfiles.DuckDb;
        var builder = new Chalk.Sources.Ado.AdoSourceBuilder(
                "down",
                () => new DuckDB.NET.Data.DuckDBConnection(
                    "DataSource=/dev/null/nowhere/chalk-redaction.duckdb"),
                "down")
            .Dialect(profile)
            .Capabilities(Chalk.Sources.Ado.AdoCapabilities.For(profile));

        builder.AddTable(
            "bars_down",
            [
                new ColumnDescriptor { Name = "symbol", Type = ChalkType.String() },
                new ColumnDescriptor { Name = "v", Type = ChalkType.Int64() },
            ]);

        return builder.Build();
    }

    private static T? Find<T>(Exception? failure)
        where T : Exception
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }

    // ------------------------------------------------------------ the recorded round trip

    /// <summary>
    /// A redaction recorded beside the plan and served back from disk is the redaction the live
    /// planner produced — the two-process determinism §3 asks for, with the fixed salt the tests
    /// always supply.
    /// </summary>
    [Fact]
    public async Task A_recorded_redaction_round_trips()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var directory = Directory.CreateTempSubdirectory("chalk-redaction-");
        try
        {
            string live;
            await using (var recording = new RecordingPlanner(sidecar.CreatePlanner(), directory.FullName))
            {
                await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = CorpusFixture.ContextId,
                    Functions = CorpusFunctions.Register,
                    Sources = Fixture.Sources,
                    Planner = recording,
                    Redaction = Salted(),
                });
                var prepared = await engine.PrepareAsync(
                    Statement, ct: TestContext.Current.CancellationToken);
                live = prepared.RedactedSql!;
            }

            await using var replay = new RecordedPlanner(directory.FullName);
            await using var replayed = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = CorpusFixture.ContextId,
                Functions = CorpusFunctions.Register,
                Sources = Fixture.Sources,
                Planner = replay,
                Redaction = Salted(),
            });

            var fromDisk = await replayed.PrepareAsync(
                Statement, ct: TestContext.Current.CancellationToken);

            Assert.Equal(live, fromDisk.RedactedSql);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A recording run that asks for no redaction writes no redaction file, which is what keeps the
    /// checked-in corpus exactly as it is.
    /// </summary>
    [Fact]
    public async Task A_recording_run_that_does_not_ask_writes_no_redaction_file()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var directory = Directory.CreateTempSubdirectory("chalk-redaction-none-");
        try
        {
            await using (var recording = new RecordingPlanner(sidecar.CreatePlanner(), directory.FullName))
            {
                await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = CorpusFixture.ContextId,
                    Functions = CorpusFunctions.Register,
                    Sources = Fixture.Sources,
                    Planner = recording,
                });
                await engine.PrepareAsync(Statement, ct: TestContext.Current.CancellationToken);
            }

            Assert.Empty(directory.GetFiles("*.redacted"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A planner with no parser says so rather than inventing an answer.</summary>
    [Fact]
    public async Task A_recorded_planner_cannot_redact_text_it_never_recorded()
    {
        // Through the interface: "this transport cannot" is the default implementation, and a
        // transport that has no parser deliberately does not override it.
        IQueryPlanner planner = new RecordedPlanner(RepoLayout.Plans.FullName);
        await using var owned = planner;

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await planner.RedactSqlAsync(new RedactSqlRequest
            {
                Sql = "SELECT 1",
                Redaction = new RedactionRequest { Salt = Salt },
            }));
    }

    /// <summary>Records every request it is given, so the "asks for nothing" property is asserted.</summary>
    private sealed class CapturingPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        public List<PlanRequest> Requests { get; } = [];

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) =>
            inner.GetInfoAsync(ct);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask RegisterCatalogAsync(
            CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(
            StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return inner.PlanAsync(request, ct);
        }

        public ValueTask<RedactedSql> RedactSqlAsync(
            RedactSqlRequest request, CancellationToken ct = default) =>
            inner.RedactSqlAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
