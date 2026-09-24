using System.Text;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The whole <c>m7-tenancy</c> family again, over design 38 §8's <b>two-source</b> layout (F84): the
/// target and the bridge in one source, the endpoint and the table its kind is held on in another,
/// and the step between them resolved through a declared association.
/// </summary>
/// <remarks>
/// <para>
/// The claim is exact and is what F84 exists to make true: for every statement and every principal,
/// in both binding modes, the two-source layout answers <b>what the co-located layout answers, row
/// for row and label for label</b>. So the family records goldens of its own —
/// <c>corpus/plans/m7-tenancy-f84</c>, which a reviewer reads as a text — and then asserts that each
/// one is byte-identical to the co-located family's. A difference is a diff in a file rather than a
/// number in a test, and there is no way for the two to drift apart quietly.
/// </para>
/// <para>
/// What may differ, and is not in the goldens: <c>row_predicate_pushed</c>, which is true only when
/// the whole marker went with the remote text (D229); the schema each step of a path names, which is
/// the point of the layout; and the SQL each source is sent, which over a POCO pair is none at all.
/// <see cref="TwoSourcePathTests"/> is where those three are read.
/// </para>
/// <para>
/// The statements are the corpus's own, with the two tables this layout moved qualified by their
/// schema — the first schema is the default one and the rest are addressed <c>schema.table</c> (A4).
/// Nothing else about a statement changes.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancySplitCorpusTests(SharedSidecar sidecar)
{
    private static readonly DirectoryInfo Goldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy-f84"));

    /// <summary>The co-located family's, which every one of this family's must equal.</summary>
    private static readonly DirectoryInfo CoLocated =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy"));

    private static readonly TenancySplitFixture Fixture = TenancySplitFixture.Shared;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            data.Add(query.Name);
        }

        return data;
    }

    /// <summary>
    /// Prepare-time binding: the oracle, the detector, this family's golden, and the co-located
    /// family's golden beside it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_principal_sees_what_the_co_located_layout_showed_them(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var sql = TenancySplitFixture.Qualified(query.Sql);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("known-limit")
            || query.Expectations.Contains("tested");
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var recorded = new StringBuilder();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            recorded.Append("-- ").Append(principal).AppendLine();
            if (refused.Contains(principal))
            {
                var refusal = await Assert.ThrowsAsync<EntitlementException>(
                    async () => await EngineRowsAsync(query, sql, context));
                detector.Inspect(new LeakScan
                {
                    Statement = $"{name} (two sources)",
                    Refusal = refusal.Message,
                });
                recorded.Append("POLICY ").AppendLine(FirstSentence(refusal.Message));
                recorded.AppendLine();
                continue;
            }

            string[] rows;
            string report;
            string[] columns;
            if (TenancyCorpusTests.KnownLeaks.TryGetValue(name, out var registered))
            {
                try
                {
                    (rows, report, columns) = await EngineRowsAsync(query, sql, context);
                }
                catch (Exception failed) when (TenancyCorpusTests.IsRegistered(failed))
                {
                    detector.Inspect(new LeakScan
                    {
                        Statement = $"{name} (two sources)",
                        Refusal = failed.Message,
                    });
                    recorded.Append(registered).Append(' ').Append(failed.GetType().Name)
                        .Append(": ").AppendLine(Stable(FirstSentence(failed.Message)));
                    recorded.AppendLine();
                    continue;
                }
            }
            else
            {
                (rows, report, columns) = await EngineRowsAsync(query, sql, context);
            }

            detector.Inspect(new LeakScan
            {
                Statement = $"{name} (two sources)",
                Rows = rows,
                Columns = columns,
                Report = report,
            });
            recorded.Append("report ").AppendLine(report);
            foreach (var row in rows)
            {
                recorded.AppendLine(row);
            }

            recorded.AppendLine();

            if (!guarded && !TenancyCorpusTests.KnownLeaks.ContainsKey(name))
            {
                Assert.Equal(await OracleRowsAsync(query, sql, context), rows);
            }
        }

        await AssertGoldenAsync(name, recorded.ToString());
    }

    /// <summary>
    /// The second binding mode: one plan for every principal, the values bound when it runs (§2,
    /// D209). The rows are the rows the same principal got from their own folded plan over the same
    /// two sources, which the theory above has already held to the oracle and to the co-located
    /// goldens.
    /// </summary>
    /// <remarks>
    /// The statements a shared plan refuses for everybody are the co-located family's own list, and
    /// they are refused here for the same reason: a leaf that folded nothing cannot know which
    /// principal will arrive, so a column some principal's rules restrict is population-only for all
    /// of them (§3.4). Whether the endpoint is a source away changes none of that, and the
    /// assertion below is that the list is the same one — a statement refused here and not there, or
    /// there and not here, is a failure.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_principal_sees_the_same_under_execute_time_binding(string name)
    {
        if (TenancyCorpusTests.KnownLeaks.ContainsKey(name))
        {
            return;
        }

        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var sql = TenancySplitFixture.Qualified(query.Sql);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("tested");
        await using var engine = await EngineAsync();

        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var folded = await FoldedAsync(engine, query, sql, context);
            var (shared, refusal) = await SharedAsync(engine, query, sql, context);
            if (shared is null)
            {
                Assert.True(
                    TenancyExecuteTimeTests.RefusedUnderAShape.Contains(name),
                    $"{name} as {principal} is refused under a shape over the two-source layout and "
                    + "is not on the co-located family's list of statements a shared plan cannot "
                    + $"take. The refusal: {refusal}");
                continue;
            }

            if (folded is null)
            {
                // A statement this principal's own folded plan refuses: the shared plan may still
                // answer, and what it answered is nothing this suite can compare.
                continue;
            }

            if (guarded)
            {
                TenancyExecuteTimeTests.AssertGuardAside(
                    folded, shared, $"{name} as {principal} (two sources)");
                continue;
            }

            Assert.Equal(Sorted(folded), Sorted(shared));
        }
    }

    private static string[] Sorted(string[] rows)
    {
        var sorted = (string[])rows.Clone();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    // ---------------------------------------------------------------- the two engines

    private async ValueTask<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [.. Fixture.Sources],
            Associations = TenancySplitFixture.Association,
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private async Task<(string[] Rows, string Report, string[] Columns)> EngineRowsAsync(
        CorpusQuery query, string sql, RequestContext context)
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(sql, context, query.PrepareOptions());
        var report = string.Join(
            ", ",
            prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}")));
        return (
            await RowsAsync(engine, prepared, query),
            report,
            [.. prepared.Columns.Select(c => c.Name)]);
    }

    private async Task<string[]> OracleRowsAsync(
        CorpusQuery query, string sql, RequestContext context)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [.. TenancySplitFixture.Oracle(context)],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
        return await RowsAsync(
            engine,
            await engine.WithEntitlements().PrepareAsync(sql, context: null, query.PrepareOptions()),
            query);
    }

    private static async Task<string[]> FoldedAsync(
        ChalkEngine engine, CorpusQuery query, string sql, RequestContext context)
    {
        try
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
            return await RowsAsync(engine, prepared, query);
        }
        catch (EntitlementException)
        {
            return null!;
        }
    }

    private static async Task<(string[]? Rows, string Refusal)> SharedAsync(
        ChalkEngine engine, CorpusQuery query, string sql, RequestContext context)
    {
        try
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context.Shape());
            var rows = new List<string>();
            await using var execution = await engine.ExecuteAsync(
                prepared.Query, context, CorpusQueries.Parameters(query.Name));
            await foreach (var batch in execution.Batches)
            {
                using (batch)
                {
                    rows.AddRange(BatchReader.ToRows(batch).Select(
                        r => LeakScan.Row(r)));
                }
            }

            return ([.. rows], "");
        }
        catch (EntitlementException refused)
        {
            return (null, refused.Message);
        }
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, CorpusQuery query)
    {
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, CorpusQueries.Parameters(query.Name));
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => LeakScan.Row(r)));
            }
        }

        return [.. rows];
    }

    // ---------------------------------------------------------------- the goldens

    private static string Stable(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, "#[0-9]+", "#N");

    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    /// <summary>
    /// This family's own golden, and the co-located family's beside it. The second assertion is the
    /// whole point of the family: the layout may move where the work happens and may not move a row
    /// or a label.
    /// </summary>
    private static async Task AssertGoldenAsync(string name, string recorded)
    {
        var file = new FileInfo(Path.Combine(Goldens.FullName, name + ".txt"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            Goldens.Create();
            await File.WriteAllTextAsync(file.FullName, recorded);
        }
        else
        {
            Assert.True(
                file.Exists,
                $"{file.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");
            Assert.Equal(
                (await File.ReadAllTextAsync(file.FullName)).ReplaceLineEndings("\n"),
                recorded.ReplaceLineEndings("\n"));
        }

        var colocated = new FileInfo(Path.Combine(CoLocated.FullName, name + ".txt"));
        Assert.True(colocated.Exists, $"{colocated.FullName} does not exist.");
        Assert.Equal(
            (await File.ReadAllTextAsync(colocated.FullName)).ReplaceLineEndings("\n"),
            recorded.ReplaceLineEndings("\n"));
    }
}
