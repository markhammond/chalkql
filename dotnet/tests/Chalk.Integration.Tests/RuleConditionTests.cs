using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using PolicyTable = Chalk.Entitlements.Tenancy.Table;

namespace Chalk.Integration.Tests;

/// <summary>
/// A policy's conditions cannot raise, and what a rule computes is evaluated only for the rows whose
/// principal it concerns.
/// </summary>
/// <remarks>
/// <para>
/// A rule whose condition could fail as it ran failed the statement quoting the row's value, even a
/// value that rule set masked from the principal (<c>'SOURCE-01'</c>); and, on a table entitled
/// through a declared path and prepared for a shape, failed it too for a principal who did not hold
/// the rule's role. A granting rule's condition that can raise now registers, and where it raises for
/// a row the rule is not applied (F302, design 82); a mask or a placeholder that can raise is refused
/// when the engine is built (F300), and should one raise all the same the result is withheld, quoting
/// nothing. The executor evaluates an <c>AND</c>'s later operand, and a
/// <c>CASE</c>'s later condition, only for the rows the earlier ones left to it.
/// </para>
/// <para>
/// A datum takes its mission from its incident, or from a column of its own. A reader holds the
/// rules below.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class RuleConditionTests(SharedSidecar sidecar)
{
    private sealed record Incident(string IncidentId, string MissionId);

    private sealed record Datum(
        string DatumId, string IncidentId, string MissionId, int RadiusNm, double Lat, string SourceIdentity);

    private static readonly Incident[] Incidents = [new("I1", "KESTREL"), new("I2", "SENTINEL")];

    private static readonly Datum[] Datums =
    [
        new("D1", "I1", "KESTREL", 2, 1.5, "SOURCE-01"),
        new("D2", "I1", "KESTREL", 5, 2.5, "SOURCE-02"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A granting rule's condition that can fail registers (F302, design 82): where it raises for a row,
    /// the rule is not applied and the row gets the next, here the <c>otherwise</c>, which withholds;
    /// where it answers, it decides as written. Nothing fails, so nothing is quoted.
    /// </summary>
    [Theory]
    [InlineData("CAST(SourceIdentity AS INTEGER) = 1", "<null>")]
    [InlineData("10 / (RadiusNm - 2) > 1", "SOURCE-02")]
    [InlineData("CAST(CASE WHEN RadiusNm = 2 THEN 'x' ELSE '0' END AS INTEGER) = 0", "SOURCE-02")]
    public async Task A_granting_rule_condition_that_can_raise_registers_and_settles_closed(string when, string second)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(
            sidecar, path: false, (t, reader, _) => t.Access(new AccessRule
            {
                Roles = [reader], Column = t.Column("SourceIdentity"), Grants = Verdict.Full, When = Sql.Of(when),
            }));

        var reader = fixture.Bind(("KESTREL", "tasking_reader"));
        Assert.Equal(
            ["D1|<null>", $"D2|{second}"],
            await fixture.WithValuesAsync("SELECT DatumId, SourceIdentity FROM s.datums ORDER BY DatumId", reader));
    }

    /// <summary>
    /// The conditions policies are written with still register and decide as they did: comparisons,
    /// connectives, an <c>IN</c> list, <c>LIKE</c>, case folding and a <c>CASE</c> over them.
    /// </summary>
    [Fact]
    public async Task The_conditions_policies_are_written_with_still_register()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, path: false, (t, reader, _) =>
        {
            t.Access(new AccessRule
            {
                Roles = [reader], Column = t.Column("SourceIdentity"), Grants = Verdict.Full,
                When = Sql.Of(
                    "RadiusNm > 3 AND UPPER(DatumId) LIKE 'D%' AND MissionId IN ('KESTREL', 'SENTINEL') "
                    + "AND CASE WHEN Lat > 2 THEN TRUE ELSE FALSE END"),
            });
            t.Access(new AccessRule
            {
                Roles = [reader], Column = t.Column("SourceIdentity"), Grants = Verdict.Mask, Mask = Sql.Of("'WITHHELD'"),
            });
        });

        var reader = fixture.Bind(("KESTREL", "tasking_reader"));
        Assert.Equal(
            ["D1|WITHHELD", "D2|SOURCE-02"],
            await fixture.WithValuesAsync("SELECT DatumId, SourceIdentity FROM s.datums ORDER BY DatumId", reader));
    }

    /// <summary>
    /// A mask that can fail is refused as the engine is built too, at the rule's place: it is
    /// evaluated in place of the value it hides, and its error would quote that value.
    /// </summary>
    [Theory]
    [InlineData("Lat", "CAST(CASE WHEN RadiusNm = 2 THEN 'x' ELSE '0' END AS DOUBLE)", "a cast to FP64 can fail")]
    [InlineData("Lat", "Lat / (RadiusNm - 2)", "arithmetic can overflow")]
    [InlineData("SourceIdentity", "SUBSTRING(SourceIdentity, 1, RadiusNm - 3)", "SUBSTRING can raise")]
    public async Task A_mask_that_can_raise_is_refused_when_the_engine_is_built(string column, string mask, string what)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var refused = await Assert.ThrowsAsync<PlanningException>(() => Fixture.CreateAsync(
            sidecar, path: false, (t, reader, _) => t.Access(new AccessRule
            {
                Roles = [reader], Column = t.Column(column), Grants = Verdict.Mask, Mask = Sql.Of(mask),
            })));

        Assert.Equal(ChalkErrorCodes.InvalidEntitlement, refused.Code);
        Assert.Contains($"({column}).rules[0].mask", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"the mask can raise an error depending on the row: {what}", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The functions masks are written with register and mask as they say, prepared with the values
    /// and for the shape, on a table entitled through its incident: a fingerprint, a prefix, a
    /// concatenation and a rounding. Each mask that reads the row is wrapped, so that an error it
    /// raised would name the column and not the value (F300).
    /// </summary>
    [Fact]
    public async Task The_functions_masks_are_written_with_register_and_mask()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(sidecar, path: true, (t, reader, _) =>
        {
            t.Access(new AccessRule
            {
                Roles = [reader], Column = t.Column("SourceIdentity"), Grants = Verdict.Mask,
                Mask = Sql.Of("SUBSTRING(SourceIdentity, 1, 3) || '-' || SUBSTRING(FINGERPRINT(SourceIdentity, 'k'), 1, 4)"),
            });
            t.Access(new AccessRule
            {
                Roles = [reader], Column = t.Column("Lat"), Grants = Verdict.Mask, Mask = Sql.Of("ROUND(Lat, 0)"),
            });
        });

        var reader = fixture.Bind(("KESTREL", "tasking_reader"));
        const string sql = "SELECT DatumId, SourceIdentity, Lat FROM s.datums ORDER BY DatumId";
        var rows = await fixture.WithValuesAsync(sql, reader);
        Assert.Equal(rows, await fixture.ForShapeAsync(sql, reader));
        Assert.Equal(2, rows.Length);
        Assert.Matches("^D1\\|SOU-[0-9a-f]{4}\\|2$", rows[0]);
        Assert.Matches("^D2\\|SOU-[0-9a-f]{4}\\|[23]$", rows[1]);
        Assert.NotEqual(rows[0].Split('|')[1], rows[1].Split('|')[1]);

        Assert.Contains("s.datums", await fixture.QuarantineAsync(sql, reader), StringComparison.Ordinal);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private TenancyEntitlements _entitlements = null!;
        private ChalkEngine _engine = null!;
        private Kind _mission;

        public static async Task<Fixture> CreateAsync(
            SharedSidecar sidecar, bool path, Action<PolicyTable, Role, Role> rules)
        {
            var fixture = new Fixture();
            var catalog = new CatalogContext { ContextId = "rule-conditions", Schemas = [Source(null).DescribeSchema()] };
            var policy = TenancyPolicy.Declare(catalog);
            fixture._mission = policy.Tenancy("mission");
            var mission = fixture._mission;
            var reader = policy.Role("tasking_reader");
            var rescuer = policy.Role("rescuer");
            var incidents = policy.Source("s").Table("incidents");
            var datums = policy.Source("s").Table("datums");
            incidents.Tenancy(t => t.Direct(mission, incidents.Column("MissionId")));
            datums.Tenancy(t =>
            {
                if (path)
                {
                    t.Inherited(mission).Through(incidents);
                }
                else
                {
                    t.Direct(mission, datums.Column("MissionId"));
                }
            });
            rules(datums, reader, rescuer);
            fixture._entitlements = policy.Compile(catalog);

            fixture._engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "rule-conditions",
                Sources = [Source(fixture._entitlements)],
                Planner = sidecar.CreatePlanner(),
            }, Ct);
            return fixture;
        }

        public RequestContext Bind(params (string Mission, string Role)[] grants) => _entitlements.Bind(new TenancyPrincipal
        {
            User = "u",
            Grants = [.. grants.Select(g => Grant.ForTenancy(_mission, g.Mission, _entitlements.Policy.Role(g.Role)))],
        });

        public async Task<string[]> WithValuesAsync(string sql, RequestContext context)
        {
            var prepared = await _engine.WithEntitlements().PrepareAsync(sql, context, ct: Ct);
            await using var execution = await _engine.ExecuteAsync(prepared.Query, ct: Ct);
            return await RowsAsync(execution);
        }

        public async Task<string[]> ForShapeAsync(string sql, RequestContext context)
        {
            var prepared = await _engine.WithEntitlements().PrepareAsync(sql, context.Shape(), ct: Ct);
            await using var execution = await _engine.ExecuteAsync(prepared.Query, context, ct: Ct);
            return await RowsAsync(execution);
        }

        /// <summary>The tables of the plan's quarantines (F302), comma separated.</summary>
        public async Task<string> QuarantineAsync(string sql, RequestContext context)
        {
            var prepared = await _engine.WithEntitlements().PrepareAsync(sql, context, ct: Ct);
            var found = new List<string>();
            Walk(prepared.Query.Plan.Root);
            return string.Join(", ", found);

            void Walk(Chalk.Ir.Rel rel)
            {
                if (rel.Quarantine.Length > 0)
                {
                    found.Add(rel.Quarantine);
                }

                foreach (var input in Chalk.Ir.PlanWalker.Inputs(rel))
                {
                    Walk(input);
                }
            }
        }

        public ValueTask DisposeAsync() => _engine.DisposeAsync();

        private static async Task<string[]> RowsAsync(QueryExecution execution)
        {
            var rows = new List<string>();
            await foreach (var batch in execution.Batches.WithCancellation(Ct))
            {
                using (batch)
                {
                    rows.AddRange(batch.ToRows().Select(r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
                }
            }

            return [.. rows];
        }

        private static PocoSource Source(TenancyEntitlements? entitlements) =>
            new PocoSourceBuilder("s", "s")
                .AddTable("incidents", Incidents, t =>
                {
                    t.UniqueKey(x => x.IncidentId);
                    if (entitlements?.For("s", "incidents") is { } d) t.Entitlement(d);
                })
                .AddTable("datums", Datums, t =>
                {
                    t.UniqueKey(x => x.DatumId)
                        .ForeignKey<Incident>(x => x.IncidentId, to: "incidents", i => i.IncidentId, verify: true);
                    if (entitlements?.For("s", "datums") is { } d) t.Entitlement(d);
                })
                .Build();
    }
}
