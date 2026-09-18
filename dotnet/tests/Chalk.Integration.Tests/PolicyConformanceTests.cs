using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources.Conformance;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The policy conformance run (<c>docs/design/16-entitlements.md</c> §7, §9): every
/// <c>m7-tenancy</c> statement as every principal, under both executors, with the plan held to what
/// executed.
/// </summary>
/// <remarks>
/// <para>
/// The corpus already compares rows against an independent disclosure of the same model. What the
/// claims harness adds is the other half of §9's exit criterion: the planner <em>reported</em> a
/// label per column and a verdict per table, and the values that came back either bear those out or
/// they do not. A row comparison alone cannot tell a correct plan from one that disclosed the same
/// rows for the wrong reason, and a label nothing checks is a comment.
/// </para>
/// <para>
/// The harness itself ships in <c>Chalk.Sources.Conformance</c>, in values and names rather than in
/// Chalk's own client types, so a third-party adapter can drive it from whatever it already has.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PolicyConformanceTests(SharedSidecar sidecar)
{
    /// <summary>
    /// Every statement the corpus holds to the oracle. One it only <em>records</em>, because the
    /// defect it found is registered (<see cref="TenancyCorpusTests.KnownLeaks"/>), has no answer
    /// for this harness to check a plan against.
    /// </summary>
    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            if (!TenancyCorpusTests.KnownLeaks.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// The same, less the statements a binding time that leaves a name open refuses for a
    /// registered reason (<see cref="TenancyCorpusTests.RefusedUnderOpenBinding"/>).
    /// </summary>
    public static TheoryData<string> OpenBindingQueries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            if (!TenancyCorpusTests.KnownLeaks.ContainsKey(query.Name)
                && !TenancyCorpusTests.RefusedUnderOpenBinding.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_principal_s_plan_says_what_executed(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("tested");
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var findings = new List<ConformanceFinding>();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            if (refused.Contains(principal))
            {
                continue;
            }

            findings.AddRange(PolicyConformance.Check(
                await CaseAsync(
                    name,
                    query.Sql,
                    principal,
                    context,
                    guarded,
                    parameters: CorpusQueries.Parameters(name))));
        }

        Assert.Empty(findings.Select(f => f.ToString()));
    }

    /// <summary>
    /// And the same statements under the <b>reference</b> executor, which shares no operator and no
    /// kernel with the vectorised one: two engines held to one plan's claims.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_reference_executor_answers_the_same_claims(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("tested");
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var findings = new List<ConformanceFinding>();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            if (refused.Contains(principal))
            {
                continue;
            }

            findings.AddRange(PolicyConformance.Check(
                await CaseAsync(
                    name,
                    query.Sql,
                    principal,
                    context,
                    guarded,
                    reference: true,
                    parameters: CorpusQueries.Parameters(name))));
        }

        Assert.Empty(findings.Select(f => f.ToString()));
    }

    /// <summary>
    /// And the same statements under <b>partial binding</b> (§2.1, D232) and the reference executor:
    /// the tenancy folded into the plan, the subject bound per execution, one leaf carrying both.
    /// </summary>
    /// <remarks>
    /// What the harness asks here is the question partial binding makes interesting. A leaf whose
    /// row predicate is half a literal IN list and half a membership marker still has to label every
    /// column for what the values turn out to be, and a marker that leaves a column
    /// <c>PerRow</c> where the fold would have said <c>Masked</c> is honest only if the rows bear it
    /// out.
    /// </remarks>
    [Theory]
    [MemberData(nameof(OpenBindingQueries))]
    public async Task Partial_binding_answers_the_same_claims(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("tested");
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var findings = new List<ConformanceFinding>();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            if (refused.Contains(principal))
            {
                continue;
            }

            findings.AddRange(PolicyConformance.Check(
                await CaseAsync(
                    name,
                    query.Sql,
                    principal,
                    context,
                    guarded,
                    reference: true,
                    prepareWith: TenancyFixture.PartiallyBound(context),
                    parameters: CorpusQueries.Parameters(name))));
        }

        Assert.Empty(findings.Select(f => f.ToString()));
    }

    /// <summary>
    /// The harness is not vacuous: a case whose label is wrong about its own values is a finding.
    /// </summary>
    [Fact]
    public async Task A_label_that_disagrees_with_the_values_is_a_finding()
    {
        var honest = await CaseAsync(
            "members", "SELECT id, first_name FROM members ORDER BY id", "u2", TenancyFixture.U2,
            guarded: false);
        Assert.Empty(PolicyConformance.Check(honest));

        // The same case with first_name relabelled: the values are masks and the label says they
        // are not, which is exactly the misclassification a row comparison cannot see.
        var lying = new PolicyCase
        {
            Name = honest.Name,
            Principal = honest.Principal,
            Columns =
            [
                honest.Columns[0],
                new PolicyColumn { Name = "first_name", Reported = "Redacted" },
            ],
            Rows = honest.Rows,
            DisclosedRows = honest.DisclosedRows,
            Tables = honest.Tables,
        };

        var finding = Assert.Single(PolicyConformance.Check(lying));
        Assert.Equal(ConformanceOutcome.Fail, finding.Outcome);
        Assert.Contains("labels_agree_with_values", finding.Subject, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- building one case

    private async Task<PolicyCase> CaseAsync(
        string name,
        string sql,
        string principal,
        RequestContext context,
        bool guarded,
        bool reference = false,
        RequestContext? prepareWith = null,
        IReadOnlyList<object?>? parameters = null)
    {
        await using var engine = await EngineAsync(reference);
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, prepareWith ?? context);

        // A plan that left half its context open is executed with the values (D232); one that folded
        // everything carries them and takes none.
        var (rows, metadata) = await RowsAsync(
            engine, prepared, prepareWith is null ? null : context, parameters);
        var columns = new List<PolicyColumn>(prepared.Columns.Count);
        for (var i = 0; i < prepared.Columns.Count; i++)
        {
            columns.Add(new PolicyColumn
            {
                Name = prepared.Columns[i].Name,
                Reported = prepared.Columns[i].Disclosure.ToString(),
                ArrowMetadata = i < metadata.Count ? metadata[i] : null,
            });
        }

        var tables = prepared.Entitlements.Tables
            .Select(t => new PolicyTable
            {
                Table = t.Table,
                Visibility = t.Visibility.ToString(),
                RowPredicatePushed = t.RowPredicatePushed,
            })
            .ToArray();

        return new PolicyCase
        {
            Name = name,
            Principal = principal,
            Columns = columns,
            Rows = rows,
            // A statement whose shape carries a group-size guard has no oracle: suppression is a
            // property of the statement rather than of a row, and an oracle that took the statement
            // apart to find the guarded calls would be the pass written a second time.
            DisclosedRows = guarded ? null : await OracleRowsAsync(sql, context, parameters),
            Tables = tables,
        };
    }

    private async Task<ChalkEngine> EngineAsync(bool reference = false) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
            Execution = reference
                ? new ExecutionOptions { Engine = ExecutionEngine.Reference }
                : new ExecutionOptions(),
        });

    private async Task<IReadOnlyList<IReadOnlyList<object?>>> OracleRowsAsync(
        string sql, RequestContext context, IReadOnlyList<object?>? parameters = null)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyOracle.Disclose(context)],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
        return (await RowsAsync(
            engine,
            await engine.WithEntitlements().PrepareAsync(sql),
            executeWith: null,
            parameters)).Rows;
    }

    private static async Task<(IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<string?> Metadata)>
        RowsAsync(
            ChalkEngine engine,
            PreparedQuery prepared,
            RequestContext? executeWith,
            IReadOnlyList<object?>? parameters = null)
    {
        var rows = new List<IReadOnlyList<object?>>();
        var metadata = new List<string?>();
        await using var execution = executeWith is null
            ? await engine.ExecuteAsync(prepared, parameters)
            : await engine.ExecuteAsync(prepared, executeWith, parameters);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                if (metadata.Count == 0)
                {
                    foreach (var field in batch.Schema.FieldsList)
                    {
                        metadata.Add(
                            field.Metadata is not null
                            && field.Metadata.TryGetValue("chalk.disclosure", out var value)
                                ? value
                                : null);
                    }
                }

                rows.AddRange(BatchReader.ToRows(batch));
            }
        }

        return (rows, metadata);
    }
}
