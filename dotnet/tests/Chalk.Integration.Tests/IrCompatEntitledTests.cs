using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.TestKit;
using Google.Protobuf;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitled plans of <c>corpus/ir-compat</c>, replayed through <b>I-IR-E</b> with the catalog
/// they were planned against (<c>docs/design/16-entitlements.md</c> §3.10, D201).
/// </summary>
/// <remarks>
/// <para>
/// <c>IrCompatReplayTests</c> replays every recorded plan structurally, with no catalog and therefore
/// with the entitlement invariant switched off — a caller that holds no catalog has nothing to
/// re-establish from. These are replayed <em>with</em> one, so what a frozen plan says about its own
/// disclosures is checked by the client that will one day still have to read it. That is what a
/// compatibility corpus is for: the invariant is a promise about plans this client accepts, and a
/// promise nobody replays is a comment.
/// </para>
/// <para>Regenerate with <c>CHALK_WRITE_FIXTURES=1</c> and read the diff.</para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class IrCompatEntitledTests(SharedSidecar sidecar)
{
    private static readonly DirectoryInfo Recorded =
        new(Path.Combine(RepoLayout.IrCompat.FullName, "v" + IrVersion.Current, "entitled"));

    /// <summary>
    /// One statement per shape the invariant has something to say about: a masked column and a
    /// withheld one at the root, a guarded population aggregate, a join of two entitled tables, a
    /// self-join whose two occurrences disclose differently, and a set operation.
    /// </summary>
    public static TheoryData<string, string, string> Statements() => new()
    {
        { "members-star", "SELECT * FROM members ORDER BY id", "u1" },
        { "members-masked", "SELECT id, first_name FROM members ORDER BY id", "u2" },
        { "orders-guarded", "SELECT org_id, AVG(amount) FROM orders GROUP BY org_id", "u6" },
        {
            "members-join-orders",
            "SELECT m.last_name, o.note FROM members m JOIN orders o ON o.member_id = m.id ORDER BY m.id",
            "u1"
        },
        {
            "members-self-join",
            "SELECT a.first_name AS any_name, b.first_name AS subject_name FROM members a JOIN "
            + "(SELECT org_id, first_name FROM members WHERE id = 3 AND org_id = 2) b "
            + "ON a.org_id = b.org_id",
            "u1"
        },
        {
            "members-union",
            "SELECT first_name FROM members WHERE org_id = 1 UNION ALL "
            + "SELECT first_name FROM members WHERE org_id = 2",
            "u1"
        },
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public async Task A_recorded_entitled_plan_still_satisfies_the_invariant(
        string name, string sql, string principal)
    {
        var context = TenancyFixture.Principals.Single(p => p.Name == principal).Context;
        var file = new FileInfo(Path.Combine(Recorded.FullName, name + ".binpb"));

        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            await using var engine = await EngineAsync();
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
            Recorded.Create();
            await File.WriteAllBytesAsync(file.FullName, prepared.Plan.ToByteArray());
            return;
        }

        Assert.True(
            file.Exists,
            $"{file.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");

        var plan = Plan.Parser.ParseFrom(await File.ReadAllBytesAsync(file.FullName));

        // The catalog is this client's own — the same question the engine asks of it at prepare —
        // and nothing of the planner's report is supplied, because a frozen plan has none.
        PlanValidator.Validate(
            plan,
            new PlanValidationOptions { EntitledTables = EntitledColumnCount });

        Assert.NotEmpty(PlanPrinter.Print(plan));
    }

    /// <summary>
    /// And the recording is not vacuous: every plan here really does read an entitled table and say
    /// so, which is what makes replaying it worth anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task A_recorded_entitled_plan_carries_the_rewrite_s_own_verdicts(
        string name, string sql, string principal)
    {
        _ = sql;
        _ = principal;
        var file = new FileInfo(Path.Combine(Recorded.FullName, name + ".binpb"));
        if (!file.Exists)
        {
            return;
        }

        var plan = Plan.Parser.ParseFrom(await File.ReadAllBytesAsync(file.FullName));
        Assert.Contains("entitled", PlanPrinter.Print(plan), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    private static int? EntitledColumnCount(TableRef table)
    {
        foreach (var schema in TenancyFixture.Shared.Catalog.Schemas)
        {
            foreach (var declared in schema.Tables)
            {
                if (string.Equals(declared.Name, table.Table, StringComparison.OrdinalIgnoreCase))
                {
                    return declared.Entitlement is null ? null : declared.Columns.Count;
                }
            }
        }

        return null;
    }

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
}
