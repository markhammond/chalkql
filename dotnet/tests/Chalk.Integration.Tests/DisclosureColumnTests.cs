using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The sibling <c>__disclosure</c> columns (<c>docs/design/16-entitlements.md</c> §3.12, D207): what
/// each column disclosed, row by row, beside the column itself.
/// </summary>
/// <remarks>
/// The per-column report is what a caller reads once; a sibling is what a grid reads per cell, and
/// for a mixed-rights principal the two say different things — the report says <c>PerRow</c> and the
/// sibling says which row was which. Both binding times are exercised, because the sibling's value is
/// the leaf's own rule conditions carried to the root, and that is the machinery execute-time binding
/// needed too.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class DisclosureColumnTests(SharedSidecar sidecar)
{
    private static readonly EntitlementsOptions Siblings =
        new() { IncludeDisclosureColumns = true };

    [Fact]
    public async Task A_mixed_principal_is_told_which_rows_were_full_and_which_were_masked()
    {
        // u1 manages O1 and is an agent in O2: O1's two members come back whole, O2's three as
        // initials, and the sibling says so per row. The whole values are read off the fixture,
        // because every value the policy can mask carries a canary (D252).
        var rows = await RowsAsync(
            "SELECT id, first_name FROM members ORDER BY id", TenancyFixture.U1, Siblings);

        // id is a column of an entitled table too, so it takes a sibling of its own: every column
        // with an entitled origin does, which is what gives a consumer one code path.
        Assert.Equal(
            [
                $"1|FULL|{Member(1).FirstName}|FULL",
                $"2|FULL|{Member(2).FirstName}|FULL",
                "3|FULL|T|MASKED",
                "4|FULL|A|MASKED",
                "7|FULL|T|MASKED",
            ],
            rows);
    }

    [Fact]
    public async Task The_sibling_is_emitted_even_where_the_disclosure_is_constant()
    {
        // u2 is an agent in O1 and nothing else: every visible row is masked, the report says
        // Masked once, and the sibling still says it per row so a consumer has one code path.
        var rows = await RowsAsync(
            "SELECT first_name FROM members ORDER BY id", TenancyFixture.U2, Siblings);
        Assert.Equal(["T|MASKED", "B|MASKED"], rows);
    }

    [Fact]
    public async Task A_redacted_column_says_so_rather_than_looking_like_a_NULL()
    {
        var rows = await RowsAsync(
            "SELECT national_id FROM members ORDER BY id", TenancyFixture.U2, Siblings);
        Assert.Equal(["<null>|REDACTED", "<null>|REDACTED"], rows);
    }

    [Fact]
    public async Task A_derived_column_takes_the_per_row_meet_of_its_origins()
    {
        // first_name is full in O1 for u1 and masked in O2; national_id is redacted everywhere. The
        // meet is the least of them, per row, so the derived column is redacted throughout.
        var rows = await RowsAsync(
            "SELECT first_name || COALESCE(national_id, '') AS whole FROM members ORDER BY id",
            TenancyFixture.U1,
            Siblings);
        Assert.All(rows, row => Assert.EndsWith("|REDACTED", row, StringComparison.Ordinal));

        // Two origins that differ per row: last_name follows first_name's rules exactly, so the meet
        // is full in O1's rows and masked in O2's.
        var both = await RowsAsync(
            "SELECT first_name || last_name AS whole FROM members ORDER BY id",
            TenancyFixture.U1,
            Siblings);
        Assert.Equal(
            [
                $"{Member(1).FirstName}{Member(1).LastName}|FULL",
                $"{Member(2).FirstName}{Member(2).LastName}|FULL",
                "TE|MASKED",
                "AR|MASKED",
                "TV|MASKED",
            ],
            both);
    }

    [Fact]
    public async Task A_column_of_an_unentitled_table_has_no_sibling()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements(Siblings)
            .PrepareAsync("SELECT name FROM symbols", TenancyFixture.U1);
        Assert.Equal(["name"], prepared.Query.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task The_siblings_own_report_label_is_full()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements(Siblings)
            .PrepareAsync("SELECT first_name FROM members", TenancyFixture.U2);

        Assert.Equal(
            ["first_name", "first_name__disclosure"], prepared.Columns.Select(c => c.Name));
        Assert.Equal(
            [ReportedDisclosure.Masked, ReportedDisclosure.Full],
            prepared.Columns.Select(c => c.Disclosure));
    }

    [Fact]
    public async Task A_suffixed_name_the_statement_already_produces_is_refused_at_prepare()
    {
        await using var engine = await EngineAsync();
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine
                .WithEntitlements(Siblings)
                .PrepareAsync(
                    "SELECT first_name, id AS \"first_name__disclosure\" FROM members",
                    TenancyFixture.U1)
                .AsTask());
        Assert.Contains("first_name__disclosure", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("refused rather than", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_suffix_is_the_one_the_columns_take()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements(new EntitlementsOptions
            {
                IncludeDisclosureColumns = true,
                DisclosureColumnSuffix = "_d",
            })
            .PrepareAsync("SELECT first_name FROM members", TenancyFixture.U2);
        Assert.Equal(["first_name", "first_name_d"], prepared.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Execute_time_binding_gives_the_same_siblings_as_prepare_time_binding()
    {
        const string sql = "SELECT id, first_name FROM members ORDER BY id";
        var folded = await RowsAsync(sql, TenancyFixture.U1, Siblings);

        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements(Siblings)
            .PrepareAsync(sql, TenancyFixture.U1.Shape());
        var shared = await ReadAsync(engine, prepared.Query, TenancyFixture.U1);

        Assert.Equal(folded, shared);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>A member's row, so a protected value is read off the fixture and never restated.</summary>
    private static TenancyFixture.Member Member(int id) =>
        TenancyFixture.Members.Single(m => m.Id == id);

    private async ValueTask<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private async Task<string[]> RowsAsync(
        string sql, RequestContext context, EntitlementsOptions options)
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements(options).PrepareAsync(sql, context);
        return await ReadAsync(engine, prepared.Query, executeWith: null);
    }

    private static async Task<string[]> ReadAsync(
        ChalkEngine engine, PreparedQuery prepared, RequestContext? executeWith)
    {
        var rows = new List<string>();
        await using var execution = executeWith is null
            ? await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null)
            : await engine.ExecuteAsync(prepared, executeWith);
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
