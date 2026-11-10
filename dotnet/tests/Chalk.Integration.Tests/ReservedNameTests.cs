using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Names that SQL reserves, used as names: a host's schema comes from types and databases it does
/// not control, so ChalkQL takes such a name as the name it is wherever that is unambiguous, and the
/// host renames nothing.
/// </summary>
/// <remarks>
/// <para>
/// A POCO property named <c>Open</c>, <c>At</c> or <c>Value</c>, or a schema named <c>national</c>,
/// was a parse error in every statement that wrote it unquoted, and in every policy expression:
/// the ones the host wrote, and the ones the policy compiler wrote itself, such as
/// <c>At IN (@ctx.mission_reader)</c> for a tenancy column, which failed the engine's creation.
/// A policy's parse error named neither the word nor the expression, and counted lines and columns
/// in text the host never wrote.
/// </para>
/// <para>
/// Now the compiler quotes the names it writes where they need it; a parse that stops at a reserved
/// word naming a schema, table or column quotes it and parses again; a reserved word that names
/// nothing is refused saying how to quote it; and a word SQL reads as a function without
/// parentheses, such as <c>USER</c>, is refused as ambiguous where a column of that name is in scope.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ReservedNameTests(SharedSidecar sidecar)
{
    private sealed record Datum(string DatumId, string Mission, bool Open, DateTime At, double Lat, string Value, string User);

    private static readonly Datum[] Data =
    [
        new("D1", "KESTREL", true, new DateTime(2026, 11, 10, 9, 0, 0), -7.4, "first", "ann"),
        new("D2", "SENTINEL", false, new DateTime(2026, 11, 10, 10, 0, 0), 51.5, "second", "bob"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- statements ----

    /// <summary>
    /// A reserved word that names a column or a schema is read as that name, unquoted: in a filter,
    /// in a projection, as a schema and qualified by a table.
    /// </summary>
    [Theory]
    [InlineData("SELECT DatumId FROM national.datums WHERE Open", "D1")]
    [InlineData("SELECT DatumId FROM national.datums WHERE At > TIMESTAMP '2026-11-10 09:30:00'", "D2")]
    [InlineData("SELECT d.DatumId FROM national.datums d WHERE d.Value = 'first' AND NOT d.Open IS NULL", "D1")]
    [InlineData("SELECT DatumId FROM national.datums WHERE open AND at < TIMESTAMP '2026-11-10 09:30:00'", "D1")]
    public async Task A_reserved_word_that_names_a_column_or_schema_is_read_as_that_name(string sql, string expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(entitlements: null);

        Assert.Equal([expected], await IdsAsync(engine, await engine.PrepareAsync(sql, ct: Ct)));
    }

    /// <summary>A reserved word in a projection reads the column, and is named as it.</summary>
    [Fact]
    public async Task A_projected_reserved_word_reads_the_column()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(entitlements: null);

        var prepared = await engine.PrepareAsync("SELECT DatumId, Value FROM national.datums ORDER BY DatumId", ct: Ct);

        Assert.Equal("Value", prepared.OutputSchema.FieldsList[1].Name);
        var rows = await RowsAsync(engine, prepared);
        Assert.Equal(["first", "second"], rows.Select(r => r[1]));
    }

    /// <summary>
    /// A reserved word that names nothing is refused saying so and how to quote it, and the parser's
    /// list of what it expected is cut short.
    /// </summary>
    [Fact]
    public async Task A_reserved_word_that_names_nothing_is_refused_saying_how_to_quote_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(entitlements: null);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync("SELECT DatumId AS window FROM national.datums", ct: Ct).AsTask());

        Assert.Equal(PlanErrorKinds.Parse, error.Kind);
        Assert.Contains("'window' is a reserved word: to use it as a name, quote it as \"window\".", error.Message, StringComparison.Ordinal);
        Assert.Matches(@"and \d+ more", error.Message);
        Assert.True(error.Message.Length < 1000, error.Message);
    }

    /// <summary>
    /// <c>USER</c> parses as SQL's function wherever it is written unquoted, so it would never read a
    /// column named <c>User</c>: such a statement is refused as ambiguous, and quoted it runs.
    /// </summary>
    [Fact]
    public async Task A_niladic_word_that_names_a_column_in_scope_is_refused_as_ambiguous()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(entitlements: null);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync("SELECT DatumId FROM national.datums WHERE user = 'ann'", ct: Ct).AsTask());
        Assert.Contains("'User'", error.Message, StringComparison.Ordinal);
        Assert.Contains("quote it as \"User\" to read the column", error.Message, StringComparison.Ordinal);

        Assert.Equal(
            ["D1"],
            await IdsAsync(engine, await engine.PrepareAsync("SELECT DatumId FROM national.datums WHERE \"User\" = 'ann'", ct: Ct)));
    }

    // ---- policies ----

    /// <summary>
    /// A tenancy column named <c>At</c>'s generated predicate is quoted, so the engine is made and
    /// each principal reads their own mission's rows; so is a mask template filled in for a column
    /// named <c>Value</c>.
    /// </summary>
    [Fact]
    public async Task The_names_the_policy_compiler_writes_are_quoted_where_they_need_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (entitlements, bind) = Policy(
            tenancyColumn: "Mission",
            configure: (table, reader) => table.Access(new AccessRule
            {
                Roles = [reader], Column = table.Column("Value"), Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
            }));
        await using var engine = await EngineAsync(entitlements);

        var rows = await RowsAsync(engine, (await engine.WithEntitlements()
            .PrepareAsync("SELECT DatumId, \"Value\" FROM national.datums", bind(["KESTREL"]), ct: Ct)).Query);
        Assert.Equal([["D1", "f"]], rows.Select(r => new[] { r[0], r[1] }));

        var descriptor = entitlements.For("national", "datums")!;
        Assert.Contains("SUBSTRING(\"Value\", 1, 1)", descriptor.Columns.Single(c => c.Column == Ordinal("Value")).Rules[0].Mask, StringComparison.Ordinal);
    }

    /// <summary>A tenancy column whose name SQL reserves is written quoted in the row predicate.</summary>
    [Fact]
    public async Task A_tenancy_column_named_after_a_reserved_word_entitles_its_rows()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (entitlements, bind) = Policy(tenancyColumn: "Value", configure: null);
        await using var engine = await EngineAsync(entitlements);

        Assert.StartsWith("((\"Value\" IN (", entitlements.For("national", "datums")!.RowPredicate, StringComparison.Ordinal);
        var prepared = await engine.WithEntitlements().PrepareAsync("SELECT DatumId FROM national.datums", bind(["second"]), ct: Ct);
        Assert.Equal(["D2"], await IdsAsync(engine, prepared.Query));
    }

    /// <summary>A rule's condition that names a column by a reserved word, unquoted, reads the column.</summary>
    [Fact]
    public async Task A_rules_condition_may_name_a_column_by_a_reserved_word()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (entitlements, bind) = Policy(
            tenancyColumn: "Mission",
            configure: (table, reader) => table.Access(new AccessRule
            {
                Roles = [reader], Column = table.Column("Lat"), Grants = Verdict.Full, When = Sql.Of("Open"),
            }));
        await using var engine = await EngineAsync(entitlements);

        var context = bind(["KESTREL", "SENTINEL"]);
        var rows = await RowsAsync(engine, (await engine.WithEntitlements()
            .PrepareAsync("SELECT DatumId, Lat FROM national.datums ORDER BY DatumId", context, ct: Ct)).Query);
        Assert.Equal(-7.4, rows[0][1]);
        Assert.NotEqual(51.5, rows[1][1]);
    }

    /// <summary>
    /// A reserved word that names nothing in a rule's condition is refused naming the word, how to
    /// quote it, and the condition as the host wrote it, with no line or column counted in text the
    /// host never wrote.
    /// </summary>
    [Fact]
    public async Task A_rules_condition_with_a_reserved_word_that_names_nothing_is_refused_naming_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (entitlements, _) = Policy(
            tenancyColumn: "Mission",
            configure: (table, reader) => table.Access(new AccessRule
            {
                Roles = [reader], Column = table.Column("Lat"), Grants = Verdict.Full, When = Sql.Of("Lat > 0 AND window"),
            }));

        var error = await Assert.ThrowsAnyAsync<Chalk.ChalkException>(async () => await EngineAsync(entitlements));

        Assert.Contains("'window' is a reserved word: to use it as a name, quote it as \"window\".", error.Message, StringComparison.Ordinal);
        Assert.Contains("window", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(" at line ", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 2000, error.Message);
    }

    /// <summary>
    /// <c>user</c> in a rule's condition is SQL's <c>USER</c>, never the column: refused as ambiguous
    /// where the table has a column of that name.
    /// </summary>
    [Fact]
    public async Task A_rules_condition_with_a_niladic_word_naming_a_column_is_refused_as_ambiguous()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (entitlements, _) = Policy(
            tenancyColumn: "Mission",
            configure: (table, reader) => table.Access(new AccessRule
            {
                Roles = [reader], Column = table.Column("Lat"), Grants = Verdict.Full, When = Sql.Of("user = 'ann'"),
            }));

        var error = await Assert.ThrowsAnyAsync<Chalk.ChalkException>(async () => await EngineAsync(entitlements));

        Assert.Contains("is ambiguous", error.Message, StringComparison.Ordinal);
        Assert.Contains("quote it as \"User\" to read the column", error.Message, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

    private static int Ordinal(string column) =>
        Source(null).DescribeSchema().Tables[0].Columns.ToList().FindIndex(c => c.Name == column);

    private static PocoSource Source(TenancyEntitlements? entitlements) =>
        new PocoSourceBuilder("probe", "national")
            .AddTable("datums", Data, t =>
            {
                t.UniqueKey(d => d.DatumId);
                if (entitlements?.For("national", "datums") is { } d) t.Entitlement(d);
            })
            .Build();

    private static (TenancyEntitlements Entitlements, Func<string[], RequestContext> Bind) Policy(
        string tenancyColumn, Action<Table, Role>? configure)
    {
        var catalog = new CatalogContext { ContextId = "reserved-names", Schemas = [Source(null).DescribeSchema()] };
        var policy = TenancyPolicy.Declare(catalog);
        var mission = policy.Tenancy("mission");
        var reader = policy.Role("reader");
        var table = policy.Source("national").Table("datums");
        table.Tenancy(t => t.Direct(mission, table.Column(tenancyColumn)));
        configure?.Invoke(table, reader);
        var entitlements = policy.Compile(catalog);
        return (entitlements, values => entitlements.Bind(new TenancyPrincipal
        {
            User = "u",
            Grants = [.. values.Select(v => Grant.ForTenancy(mission, v, reader))],
        }));
    }

    private async Task<ChalkEngine> EngineAsync(TenancyEntitlements? entitlements) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "reserved-names",
            Sources = [Source(entitlements)],
            Planner = sidecar.CreatePlanner(),
        }, Ct);

    private static async Task<List<string>> IdsAsync(ChalkEngine engine, PreparedQuery prepared) =>
        [.. (await RowsAsync(engine, prepared)).Select(r => (string)r[0]!)];

    private static async Task<List<object?[]>> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<object?[]>();
        await using var execution = await engine.ExecuteAsync(prepared, ct: Ct);
        await foreach (var batch in execution.Batches.WithCancellation(Ct))
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows());
            }
        }

        return rows;
    }
}
