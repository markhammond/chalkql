using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The detector, tested on itself (D252, <c>docs/design/32-adversarial-entitlements.md</c> §2). A
/// detector that never fires is a detector nobody can trust, and the corpus it guards is green by
/// construction, so the proof that it works has to be a leak somebody arranged.
/// </summary>
/// <remarks>
/// The leaking fake is the fixture's own tables with the entitlements taken off — the source really
/// does hand back the canaries, through the ordinary query path, and the principal really is one the
/// oracle discloses none of them to. Everything else here is one channel at a time, so that a
/// channel dropped from <see cref="LeakScan"/> by a later edit fails a case of its own rather than
/// quietly narrowing the detector.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class LeakDetectorTests(SharedSidecar sidecar)
{
    /// <summary>An agent in O1: they see the initial of a name and none of the canary after it.</summary>
    private static LeakDetector Agent => LeakDetector.For("u2", TenancyFixture.U2);

    [Fact]
    public async Task A_source_that_returns_a_forbidden_token_trips_the_detector()
    {
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";
        await using var engine = await LeakingEngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U2);
        var rows = await RowsAsync(engine, prepared);

        // The unentitled tables disclose the whole name, which is the leak this is about.
        Assert.Contains(TenancyFixture.Members[0].FirstName, rows[0], StringComparison.Ordinal);

        var caught = Assert.Throws<Xunit.Sdk.FailException>(() => Agent.Inspect(new LeakScan
        {
            Statement = "01_members_star (a leaking source)",
            Rows = rows,
            Columns = [.. prepared.Columns.Select(c => c.Name)],
        }));

        // The message is the report: who, which token, where, and which statement.
        Assert.Contains("u2", caught.Message, StringComparison.Ordinal);
        Assert.Contains(TenancyCanaries.Token("FIRST", 1), caught.Message, StringComparison.Ordinal);
        Assert.Contains("first_name", caught.Message, StringComparison.Ordinal);
        Assert.Contains("01_members_star", caught.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_that_returns_a_forbidden_amount_trips_the_detector()
    {
        const string Sql = "SELECT id, amount FROM orders ORDER BY id";
        await using var engine = await LeakingEngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U2);

        var rows = await RowsAsync(engine, prepared);
        var caught = Assert.Throws<Xunit.Sdk.FailException>(() => Agent.Inspect(new LeakScan
        {
            Statement = "12_orders_value_use_of_amount (a leaking source)",
            Rows = rows,
            Columns = [.. prepared.Columns.Select(c => c.Name)],
        }));

        // Order 1 is in O1, which u2 can see; its amount is one no rule of theirs discloses.
        Assert.Contains(
            TenancyFixture.Orders[0].Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            caught.Message,
            StringComparison.Ordinal);
        Assert.Contains("amount", caught.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every channel D252 names, one at a time: a token planted in each must be found, and the
    /// failure must say which channel it was found in.
    /// </summary>
    [Theory]
    [InlineData("plan")]
    [InlineData("report")]
    [InlineData("refusal")]
    [InlineData("remote")]
    [InlineData("statistics")]
    public void Every_channel_the_detector_names_is_scanned(string channel)
    {
        var planted = "a value ending " + TenancyCanaries.Token("NID", 1);
        var scan = channel switch
        {
            "plan" => new LeakScan { Statement = "planted", PlanText = planted },
            "report" => new LeakScan { Statement = "planted", Report = planted },
            "refusal" => new LeakScan { Statement = "planted", Refusal = planted },
            "remote" => new LeakScan { Statement = "planted", RemoteSql = [planted] },
            _ => new LeakScan { Statement = "planted", Statistics = planted },
        };

        var caught = Assert.Throws<Xunit.Sdk.FailException>(() => Agent.Inspect(scan));
        Assert.Contains(TenancyCanaries.Token("NID", 1), caught.Message, StringComparison.Ordinal);
        Assert.Contains("planted", caught.Message, StringComparison.Ordinal);
    }

    /// <summary>What a principal is entitled to passes, which is the other half of a detector.</summary>
    [Fact]
    public void A_value_the_oracle_discloses_is_not_a_leak()
    {
        // u1 manages O1, so O1's names — canaries and all — are theirs to receive.
        LeakDetector.For("u1", TenancyFixture.U1).Inspect(new LeakScan
        {
            Statement = "the manager's own rows",
            Rows = [$"1|{TenancyFixture.Members[0].FirstName}"],
            Columns = ["id", "first_name"],
        });

        // And the mask an agent gets carries none, which is what places the token after the initial.
        Agent.Inspect(new LeakScan
        {
            Statement = "the agent's masks",
            Rows = ["1|T", "2|B"],
            Columns = ["id", "first_name"],
        });
    }

    /// <summary>
    /// The universe the forbidden set is taken from: one token per distinct protected value, over
    /// every column the policy can mask or hide.
    /// </summary>
    [Fact]
    public void Every_protected_column_of_the_fixture_carries_a_canary()
    {
        string[] groups = ["FIRST", "LAST", "NID", "NOTE", "BODY", "CONTENT"];
        foreach (var group in groups)
        {
            Assert.Contains(
                LeakDetector.EveryToken,
                token => token.StartsWith(TenancyCanaries.Marker + group + "-", StringComparison.Ordinal));
        }

        // `national_id` has no rule that can ever disclose it, so its canaries are forbidden to
        // every principal — the global grant included. That is the policy, read through the oracle.
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            Assert.Throws<Xunit.Sdk.FailException>(() => detector.Inspect(new LeakScan
            {
                Statement = "a national id",
                Rows = [TenancyFixture.Members[0].NationalId],
                Columns = ["national_id"],
            }));
        }
    }

    // ---------------------------------------------------------------- the fake

    /// <summary>
    /// The same tables with no entitlement at all: a source that hands back everything it holds,
    /// which is the leak a detector exists for.
    /// </summary>
    private async Task<ChalkEngine> LeakingEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Unentitled.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private static async Task<string[]> RowsAsync(ChalkEngine engine, EntitledQuery prepared)
    {
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared.Query, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return [.. rows];
    }
}
