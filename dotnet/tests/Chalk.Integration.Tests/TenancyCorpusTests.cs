using System.Text;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitlement corpus (<c>corpus/queries/m7-tenancy</c>, <c>docs/design/16-entitlements.md</c>
/// §8, §9): every query, as every principal.
/// </summary>
/// <remarks>
/// <para>
/// Two things are asserted of every (query, principal) pair. It matches the <b>oracle</b> —
/// <see cref="TenancyOracle"/>, which states §8's model in C# and discloses the rows directly, with
/// no descriptor, no planner and no pass — which is §9's first exit criterion and the thing a
/// hand-written expectation cannot give: an oracle catches the misclassification nobody thought of.
/// And it matches a <b>recorded golden</b> holding the rows and the per-column report, so a change
/// in what a principal is told is a diff a reviewer reads rather than a number in a test.
/// </para>
/// <para>
/// A query whose statement guards a population aggregate is compared with its golden alone. The
/// guard is a property of the statement's shape rather than of a row, and an oracle that took a
/// statement apart to find the guarded calls would be the pass written a second time; the three
/// queries that need it say so in their own file.
/// </para>
/// <para>
/// Regenerate the goldens with <c>CHALK_WRITE_FIXTURES=1</c> and read the diff.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed partial class TenancyCorpusTests(SharedSidecar sidecar)
{
    private static readonly DirectoryInfo Goldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy"));

    /// <summary>The adversarial family's own goldens (D251).</summary>
    private static readonly DirectoryInfo AdversarialGoldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-adversarial"));

    /// <summary>
    /// What the battery found, by statement and F-number: a run of one of these is recorded rather
    /// than held to the oracle, so the defect stays visible in the golden and on the register while
    /// the battery stays green.
    /// </summary>
    /// <remarks>
    /// The pattern <c>NotAboutBindingTime</c> already uses, and the rule D251 states: a successful
    /// attack is an F-number and an exclusion, never a fix to the planner or the executor in this
    /// run and never a weakened statement. A weakened statement would be the pass testing itself.
    /// The seven that remain fail <em>closed</em> — a refusal, an unsupported feature or a plan the
    /// client's own validator rejects. F58, the one principal receiving an answer the oracle said
    /// they may not have, is fixed (ADR 0038): statements 08, 17 and 18 are held to the oracle as
    /// every principal like any other, and are named here no longer. So is F78 (ADR 0058 §3), which
    /// takes <c>39_orders_join_the_lines</c> off this list the same way, and F79 (ADR 0062 §2),
    /// which takes <c>54_a_correlated_count_of_the_lines_of_an_order</c> off it: a correlate whose
    /// right side no longer reads the left row is a join on TRUE, and the two principals who reach
    /// no line count zero rather than being refused.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> KnownLeaks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["14_probe_string_agg"] = "F59",
            ["16_probe_window_rank"] = "F60",
            ["24_correlated_exists_on_a_shared_value"] = "F62",
            ["25_not_exists_over_an_invisible_child"] = "F62",
            ["27_messages_through_an_invisible_thread"] = "F63",
            ["31_intersect_with_a_probe_list"] = "F64",
        };

    /// <summary>
    /// Statements no theory runs, because the defect they found is that planning does not finish.
    /// Such a statement stays in the corpus exactly as written; the register and the family's
    /// README say why it is not run.
    /// </summary>
    /// <remarks>
    /// <b>Empty since the F66 fix (ADR 0050).</b> F61's statement 23 and F65's statement 49 were
    /// the two, and both turned out to be the disease F66 was: two rules of one Hep collection
    /// undoing each other — here <c>JOIN_PUSH_TRANSITIVE_PREDICATES</c> and
    /// <c>PruneEmptyFilter</c> rather than the reduce rule, the inferred predicate for one
    /// occurrence of the table unsatisfiable, the empty <c>Values</c> rebuilt, the inference fired
    /// again. Both plan in milliseconds with the phase boundary in place. This run does what the
    /// register left to it: runs them as every principal in every theory their family runs, under
    /// the oracle and the leak detector, and records their goldens. Neither disagrees with the
    /// oracle, the detector reports nothing of either, and so neither goes back. Statement 23's
    /// <em>remote folded</em> run was the one exception, excluded there by name as <b>F72</b>; that
    /// is fixed (ADR 0059) and it runs in every theory of its family like any other statement.
    /// </remarks>
    /// <summary>
    /// Statements a binding time that leaves a name <b>open</b> refuses, by F-number: each answers
    /// under prepare-time binding, where the corpus above holds it to the oracle as every
    /// principal, and each fails <b>closed</b>, at the client or at the taint check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Empty since the F77, F81 and F78 fixes (ADR 0058).</b> The five statements clause (h)
    /// left here were three faces of one question — what a check may read back off a finished plan
    /// when what it reads is the mechanism's own — and the three answers took all five out. The
    /// list stays, because the next binding-time refusal belongs in it.
    /// </para>
    /// <para>
    /// <b>F77 is fixed</b> (ADR 0058 §1): clause 3 asks what the plan's own predicates guarantee
    /// before it asks for a context relation's scan, so <c>04_members_like_prefix</c> is held to
    /// the oracle here like any other statement and is named no longer.
    /// </para>
    /// <para>
    /// <b>F78 is fixed</b>, by F81's rule and nothing of its own (ADR 0058 §3): the verdict a
    /// declared path computes at its endpoint appears in the sanitiser's <em>conditions</em> only,
    /// never in an arm, so once a condition is no longer an origin of the value the mechanism's
    /// correlation read no longer reaches it. <c>39_orders_join_the_lines</c> answers for all
    /// fifteen principals.
    /// </para>
    /// <para>
    /// <b>F81 is fixed</b> (ADR 0058 §2): a <c>CASE</c>'s value origins are the origins of its
    /// result arms, and its conditions are a predicate position the walk checks and never meets
    /// into the value — so <c>02</c>, <c>07</c> and <c>13</c> are held to the oracle here too. All
    /// three are refused under a <em>full</em> shape by the sidecar's own clause 2, which is
    /// <b>F86</b> and is named in <see cref="TenancyExecuteTimeTests"/>'s own list.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> RefusedUnderOpenBinding =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal static readonly IReadOnlyDictionary<string, string> NotPlanned =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            data.Add(query.Name);
        }

        return data;
    }

    public static TheoryData<string> Adversarial()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Adversarial())
        {
            if (!NotPlanned.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public Task Every_principal_sees_what_the_oracle_says_and_what_the_golden_records(string name) =>
        RunAsync(
            CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name),
            Goldens,
            TenancyFixture.Shared);

    /// <summary>
    /// The adversarial battery in process (D251): every statement written to subvert the rewrite,
    /// as every principal, held to the same oracle and a golden of its own, with D252's detector
    /// over everything the principal received.
    /// </summary>
    /// <remarks>
    /// A statement never quotes a canary. The attacker controls the statement text and its
    /// parameters and nothing else (§0), so a guess is a value they could have made up; quoting a
    /// fixture value would put a forbidden token in the statement's own plan text and the detector
    /// would report the guess back as a leak.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Adversarial))]
    public Task Every_subversion_is_held_to_the_oracle_and_the_golden(string name)
    {
        var query = CorpusQueries.LoadM7Adversarial().Single(q => q.Name == name);
        Assert.DoesNotContain(TenancyCanaries.Marker, query.Sql, StringComparison.Ordinal);

        // The family's own fixture: the same tables and descriptors, with D251 class 4's two
        // functions declared beside them.
        return RunAsync(query, AdversarialGoldens, TenancyFixture.Subversion);
    }

    /// <summary>
    /// The <b>output types</b> one statement's plan hands back, as every principal (F91, ADR 0062
    /// §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The goldens above record rows and a per-column report, and neither prints a type — which is
    /// why nothing here caught F91, where a column a principal holds in full came back through a
    /// nullable cast and the plan said <c>name:STRING?</c> against a report that said <c>Full</c>.
    /// No row moved and no disclosure changed, so a golden of rows could not have seen it.
    /// </para>
    /// <para>
    /// One statement is enough, and it is the shape the finding is about: a star over
    /// <c>members</c>, whose <c>first_name</c> and <c>last_name</c> are masked for an agent, full
    /// for a manager, and withheld outside — so one golden holds a sanitiser that folds to its own
    /// column, one that does not, and a column nobody may read. What a host is handed is the
    /// <em>output schema</em>, which is what this records rather than a plan's text.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_output_types_one_statement_hands_back_are_recorded_for_every_principal()
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == "01_members_star");
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var recorded = new StringBuilder();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            recorded.Append("-- ").Append(principal).AppendLine();
            if (refused.Contains(principal))
            {
                recorded.AppendLine("POLICY");
                recorded.AppendLine();
                continue;
            }

            var prepared = await engine
                .WithEntitlements()
                .PrepareAsync(query.Sql, context, query.PrepareOptions());
            recorded.AppendLine(string.Join(
                ", ",
                prepared.OutputSchema.FieldsList.Select(
                    f => $"{f.Name}:{f.DataType.Name}{(f.IsNullable ? "?" : string.Empty)}")));
            recorded.AppendLine();
        }

        await AssertGoldenAsync(OutputTypeGoldens, query.Name, recorded.ToString());
    }

    /// <summary>Where <see cref="The_output_types_one_statement_hands_back_are_recorded_for_every_principal"/> records.</summary>
    private static readonly DirectoryInfo OutputTypeGoldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy-types"));

    private async Task RunAsync(CorpusQuery query, DirectoryInfo goldens, TenancyFixture fixture)
    {
        var name = query.Name;
        // A guarded aggregate and D253's known limit are both compared with the golden alone: the
        // first because suppression is a property of the statement's shape rather than of a row,
        // and the second because the limit is what it records.
        // `tested` joins `guarded` and `known-limit` here for the reason ADR 0042 records: the
        // oracle discloses a source, and a column that is testable and not readable is not a value
        // a source can hold. The statement is held to its golden, and to the naive model of D261 by
        // TestVerdictTests.
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
                    async () => await EngineRowsAsync(query, context, fixture));
                detector.Inspect(new LeakScan
                {
                    Statement = $"{name} (in process)",
                    Refusal = refusal.Message,
                });
                recorded.Append("POLICY ").AppendLine(FirstSentence(refusal.Message));
                recorded.AppendLine();
                continue;
            }

            string[] rows;
            string report;
            string[] columns;
            if (KnownLeaks.TryGetValue(name, out var registered))
            {
                // A registered defect: what this principal got is recorded rather than asserted, and
                // the detector still reads every word of it. Every one of them fails closed, so
                // the outcome here is usually an exception and the golden is where it stays
                // visible.
                try
                {
                    (rows, report, columns) = await EngineRowsAsync(query, context, fixture);
                }
                catch (Exception failed) when (IsRegistered(failed))
                {
                    detector.Inspect(new LeakScan
                    {
                        Statement = $"{name} (in process)",
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
                (rows, report, columns) = await EngineRowsAsync(query, context, fixture);
            }

            detector.Inspect(new LeakScan
            {
                Statement = $"{name} (in process)",
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

            if (!guarded && !KnownLeaks.ContainsKey(name))
            {
                Assert.Equal(await OracleRowsAsync(query, context, fixture), rows);
            }
        }

        await AssertGoldenAsync(goldens, name, recorded.ToString());
    }

    /// <summary>
    /// The ways a registered defect shows itself: a policy refusal, a planner error, a plan the
    /// client's own invariant check rejects, and an unsupported feature. Anything else is a failure
    /// of the run rather than the defect, and is not caught.
    /// </summary>
    internal static bool IsRegistered(Exception failed) =>
        failed is EntitlementException
            or Chalk.Client.PlanningException
            or Chalk.Ir.InvalidPlanException
            or Chalk.Sources.UnsupportedFeatureException;

    /// <summary>
    /// A registered failure's message with the planner's own node numbers taken out. Calcite
    /// numbers a <c>RelNode</c> per process, so <c>rel#73322</c> is a different number on every run
    /// and is the one thing in such a message a golden cannot hold.
    /// </summary>
    private static string Stable(string message) => NodeNumber().Replace(message, "#N");

    [System.Text.RegularExpressions.GeneratedRegex("#[0-9]+")]
    private static partial System.Text.RegularExpressions.Regex NodeNumber();

    /// <summary>The message's first sentence: the rest names the design section and is not a fact.</summary>
    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    // ---------------------------------------------------------------- the two engines

    private async Task<(string[] Rows, string Report, string[] Columns)> EngineRowsAsync(
        CorpusQuery query, RequestContext context, TenancyFixture fixture)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [fixture.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(query.Sql, context, query.PrepareOptions());
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
        CorpusQuery query, RequestContext context, TenancyFixture fixture)
    {
        // The oracle's tables carry no entitlement, so nothing here is rewritten and the context is
        // never bound: the statement is an ordinary statement over rows somebody else disclosed.
        var disclosed = TenancyOracle.Disclose(
            context, subversionFunctions: ReferenceEquals(fixture, TenancyFixture.Subversion));
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [disclosed],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
        return await RowsAsync(
            engine,
            await engine.WithEntitlements().PrepareAsync(
                query.Sql, context: null, query.PrepareOptions()),
            query);
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

    private static async Task AssertGoldenAsync(
        DirectoryInfo goldens, string name, string recorded)
    {
        var file = new FileInfo(Path.Combine(goldens.FullName, name + ".txt"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            goldens.Create();
            await File.WriteAllTextAsync(file.FullName, recorded);
            return;
        }

        Assert.True(
            file.Exists,
            $"{file.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(file.FullName)).ReplaceLineEndings("\n"),
            recorded.ReplaceLineEndings("\n"));
    }
}
