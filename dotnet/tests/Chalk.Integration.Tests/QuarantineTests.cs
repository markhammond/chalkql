using System.Globalization;
using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// What an access policy evaluates is quarantined (F302, design 82): a condition of the policy that
/// raises for a row is decided as having no answer, and the statement does not fail; what cannot be so
/// settled withholds the result, naming the table and quoting nothing; and a host's observer is handed
/// each indeterminate decision with its reason, under one correlation id.
/// </summary>
/// <remarks>
/// <c>reports</c> holds a level written as text: R1's is <c>1</c>, R2's is <c>X</c>, which does not
/// convert, and R3's is <c>3</c>. Before the quarantine a policy could not cast it at all (F292), and
/// had it been allowed to, R2's would have failed every statement over the table, quoting <c>'X'</c>.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class QuarantineTests(SharedSidecar sidecar)
{
    private sealed record Report(string ReportId, string Level, string Note, double Score);

    private static readonly Report[] Reports =
    [
        new("R1", "1", "first", 1.3),
        new("R2", "X", "second", 2.6),
        new("R3", "3", "third", 3.9),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ExecutionEngine> Engines => new() { ExecutionEngine.Vectorised, ExecutionEngine.Reference };

    /// <summary>
    /// A row predicate that casts admits the rows that convert and pass, and no other: R2's level does
    /// not convert, so R2 is not admitted, and the statement answers.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_row_predicate_that_casts_admits_what_converts_and_passes(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Decisions();
        await using var fixture = await Fixture.CreateAsync(
            sidecar, new TableEntitlementDescriptor { RowPredicate = "CAST(Level AS INTEGER) <= 2" }, executor);

        Assert.Equal(["R1"], await fixture.RowsAsync("SELECT ReportId FROM s.reports ORDER BY ReportId", observer));

        var decision = Assert.Single(observer.Seen);
        Assert.Equal(ClosedResolution.RowsNotAdmitted, decision.Resolution);
        Assert.Equal(1, decision.Occurrences);
        Assert.Equal("s.reports", decision.Tables);
        Assert.Equal(IndeterminateAt.ChalkQL, decision.At);
        Assert.Contains("X", decision.Reason, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, decision.CorrelationId);
    }

    /// <summary>
    /// Aimed at the excluded row by a condition the principal writes, which crosses the barrier: the
    /// statement neither fails nor returns it, so nothing says what R2's level holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Aimed_at_an_excluded_row_the_statement_neither_fails_nor_returns_it(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(
            sidecar, new TableEntitlementDescriptor { RowPredicate = "CAST(Level AS INTEGER) <= 2" }, executor);

        Assert.Empty(await fixture.RowsAsync("SELECT ReportId FROM s.reports WHERE ReportId = 'R2'"));
    }

    /// <summary>
    /// A granting rule whose condition casts: R2's condition has no answer, so the rule is not applied
    /// and R2's note gets the <c>otherwise</c>, withheld; R1's is shown; nothing fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_granting_rule_whose_condition_raises_falls_to_the_next(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Decisions();
        await using var fixture = await Fixture.CreateAsync(sidecar, NoteGranted("CAST(Level AS INTEGER) = 1"), executor);

        Assert.Equal(
            ["R1|first", "R2|<null>", "R3|<null>"],
            await fixture.RowsAsync("SELECT ReportId, Note FROM s.reports ORDER BY ReportId", observer));

        // The reference executor settles the row the same way and is the oracle of what is returned;
        // the decisions a host is handed are the vectorised executor's.
        if (executor == ExecutionEngine.Vectorised)
        {
            var decision = Assert.Single(observer.Seen);
            Assert.Equal(ClosedResolution.RuleNotApplied, decision.Resolution);
            Assert.Equal(1, decision.Occurrences);
        }
    }

    /// <summary>
    /// A restricting rule whose condition can raise is refused as the engine is built: on an error the
    /// row would fall through to the rule after it, which shows the note in full.
    /// </summary>
    [Fact]
    public async Task A_restricting_rule_whose_condition_can_raise_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var restricting = new TableEntitlementDescriptor
        {
            Columns =
            [
                new ColumnEntitlementDescriptor
                {
                    Column = 2,
                    Rules =
                    [
                        new DisclosureRule { When = "CAST(Level AS INTEGER) = 1", Then = Disclosure.None },
                        new DisclosureRule { When = "TRUE", Then = Disclosure.Full },
                    ],
                    Otherwise = Disclosure.None,
                },
            ],
        };

        var refused = await Assert.ThrowsAsync<PlanningException>(
            () => Fixture.CreateAsync(sidecar, restricting, ExecutionEngine.Vectorised));

        Assert.Equal(ChalkErrorCodes.InvalidEntitlement, refused.Code);
        Assert.Contains("(Note).rules[0].when", refused.Message, StringComparison.Ordinal);
        Assert.Contains("rules[1] after it discloses FULL", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mask that raises after all, through a host function that breaks its promise to be leakproof:
    /// the result is withheld with <c>PolicyIndeterminate</c>, naming the table and a correlation id
    /// and quoting nothing; the host's observer is handed the reason under the same id.
    /// </summary>
    [Fact]
    public async Task A_mask_that_raises_withholds_the_result_and_quotes_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var observer = new Decisions();
        await using var fixture = await Fixture.CreateAsync(sidecar, NoteMasked("boom(Note)"), ExecutionEngine.Vectorised);

        var withheld = await Assert.ThrowsAsync<PolicyIndeterminateException>(
            () => fixture.RowsAsync("SELECT ReportId, Note FROM s.reports ORDER BY ReportId", observer));

        Assert.Equal(ChalkErrorCodes.PolicyIndeterminate, withheld.Code);
        Assert.Equal("s.reports", withheld.Tables);
        Assert.Contains("could not reach a decision for a row", withheld.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second", withheld.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Execution failed", withheld.Message, StringComparison.Ordinal);

        var decision = Assert.Single(observer.Seen);
        Assert.Equal(ClosedResolution.StatementWithheld, decision.Resolution);
        Assert.Equal(withheld.CorrelationId, decision.CorrelationId);
        Assert.Contains("second", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// An observer that throws is counted and never let through: the statement answers as it would
    /// have, and the engine says its observer failed, by type alone.
    /// </summary>
    [Fact]
    public async Task An_observer_that_throws_is_counted_and_the_statement_answers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var broken = new Decisions { Throws = true };
        await using var fixture = await Fixture.CreateAsync(
            sidecar, new TableEntitlementDescriptor { RowPredicate = "CAST(Level AS INTEGER) <= 2" }, ExecutionEngine.Vectorised);

        Assert.Equal(["R1"], await fixture.RowsAsync("SELECT ReportId FROM s.reports ORDER BY ReportId", broken, out var entitled));
        Assert.Equal(1, entitled.IndeterminateObserverFaults);
        Assert.Equal(typeof(InvalidOperationException), entitled.LastIndeterminateObserverFault);
    }

    /// <summary>
    /// Floating-point arithmetic in a mask, which raises for no input in ChalkQL: a quarter-step
    /// rounding is accepted and computed.
    /// </summary>
    [Fact]
    public async Task A_floating_point_mask_is_accepted_and_computed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var masked = new TableEntitlementDescriptor
        {
            Columns =
            [
                new ColumnEntitlementDescriptor
                {
                    Column = 3,
                    Rules = [new DisclosureRule { When = "TRUE", Then = Disclosure.Masked, Mask = "ROUND(Score * 4, 0) / 4" }],
                    Otherwise = Disclosure.None,
                },
            ],
        };
        await using var fixture = await Fixture.CreateAsync(sidecar, masked, ExecutionEngine.Vectorised);

        Assert.Equal(
            ["R1|1.25", "R2|2.5", "R3|4"],
            await fixture.RowsAsync("SELECT ReportId, Score FROM s.reports ORDER BY ReportId"));
    }

    private static TableEntitlementDescriptor NoteGranted(string when) => new()
    {
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [new DisclosureRule { When = when, Then = Disclosure.Full }],
                Otherwise = Disclosure.None,
            },
        ],
    };

    private static TableEntitlementDescriptor NoteMasked(string mask) => new()
    {
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [new DisclosureRule { When = "TRUE", Then = Disclosure.Masked, Mask = mask }],
                Otherwise = Disclosure.None,
            },
        ],
    };

    private sealed class Decisions : IIndeterminateDecisions
    {
        public List<IndeterminateDecision> Seen { get; } = [];

        public bool Throws { get; init; }

        public void Resolved(IndeterminateDecision decision)
        {
            Seen.Add(decision);
            if (Throws)
            {
                throw new InvalidOperationException("the host's logger broke on " + decision.Reason);
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private ChalkEngine _engine = null!;

        public static async Task<Fixture> CreateAsync(
            SharedSidecar sidecar, TableEntitlementDescriptor descriptor, ExecutionEngine executor)
        {
            var fixture = new Fixture();
            fixture._engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "quarantine",
                Sources = [Source(descriptor)],
                Planner = sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { Engine = executor },
                Functions = registry => registry.AddScalar<string, string>(
                    "boom",
                    static s => s == "second" ? throw new InvalidOperationException($"boom on '{s}'") : s),
            }, Ct);
            return fixture;
        }

        public Task<string[]> RowsAsync(string sql, IIndeterminateDecisions? observer = null) =>
            RowsAsync(sql, observer, out _);

        public Task<string[]> RowsAsync(string sql, IIndeterminateDecisions? observer, out EntitledEngine entitled)
        {
            entitled = _engine.WithEntitlements(indeterminate: observer);
            return RunAsync(entitled, sql);
        }

        public ValueTask DisposeAsync() => _engine.DisposeAsync();

        private async Task<string[]> RunAsync(EntitledEngine entitled, string sql)
        {
            var context = new RequestContext
            {
                Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
                {
                    ["unused"] = new() { Columns = ["id"], Rows = [[1]], ColumnTypes = [ChalkType.Int32()] },
                },
            };
            var prepared = await entitled.PrepareAsync(sql, context, ct: Ct);
            var rows = new List<string>();
            await using var execution = await _engine.ExecuteAsync(prepared.Query, ct: Ct);
            await foreach (var batch in execution.Batches.WithCancellation(Ct))
            {
                using (batch)
                {
                    rows.AddRange(batch.ToRows().Select(r => string.Join("|", r.Select(v => v switch
                    {
                        null => "<null>",
                        double d => d.ToString(CultureInfo.InvariantCulture),
                        _ => v.ToString(),
                    }))));
                }
            }

            return [.. rows];
        }

        private static PocoSource Source(TableEntitlementDescriptor descriptor) =>
            new PocoSourceBuilder("s", "s")
                .AddTable("reports", Reports, t => t.UniqueKey(x => x.ReportId).Entitlement(descriptor))
                .AddFunction("boom", f => f.Scalar<string, string>("s").Strict().Leakproof().Client())
                .Build();
    }
}
