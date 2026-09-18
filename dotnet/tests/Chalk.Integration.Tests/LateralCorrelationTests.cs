using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The one <c>LATERAL</c> shape Chalk refuses (<c>docs/adr/0026-lateral-shared-outer-field.md</c>,
/// <c>docs/design/14-windows-ii.md</c> §8): a sub-query that constrains two of its own tables with
/// the same outer column. Calcite's general decorrelator rewrites it into a join that keeps only one
/// of the two equalities, so one side ranges over every row of its table and the answer is about
/// somebody else's rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the expected rows are written out by hand.</b> Chalk's differential tests compare the
/// vectorised engine against the reference executor, and both of them evaluate <em>the plan</em>. A
/// plan that lost an equality loses it for both, so a differential comparison would agree on the
/// wrong answer. The counts below are therefore derived from the SQL, from a fixture small enough to
/// count on paper, and the test asserts against those.
/// </para>
/// <para>
/// The fixture: ACME has two orders with different statuses, BRIX has two with the same status.
/// The sub-query counts ordered pairs of the customer's <em>own</em> orders that share a status —
/// so ACME has 2 (each order with itself) and BRIX has 4 (both orders with both). Under the
/// decorrelator's rewrite <c>o1</c> ranges over all four orders instead, giving 4 and 6.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class LateralCorrelationTests(SharedSidecar sidecar)
{
    private sealed record Customer(long Id, string Name);

    private sealed record Order(long Id, long CustomerId, string Status);

    private static readonly Customer[] Customers =
    [
        new(1, "ACME"),
        new(2, "BRIX"),
    ];

    private static readonly Order[] Orders =
    [
        new(10, 1, "open"),
        new(11, 1, "shut"),
        new(12, 2, "open"),
        new(13, 2, "open"),
    ];

    /// <summary>The shape: <c>c.id</c> constrains both <c>o1</c> and <c>o2</c>.</summary>
    private const string SharedOuterColumn = """
        SELECT c.name, x.n
        FROM customers c LEFT JOIN LATERAL (
          SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2 ON o1.status = o2.status
          WHERE o1.customer_id = c.id AND o2.customer_id = c.id) x ON TRUE
        ORDER BY c.name
        """;

    /// <summary>The way out the refusal names: constrain <c>o2</c> through <c>o1</c>.</summary>
    private const string RewrittenThroughO1 = """
        SELECT c.name, x.n
        FROM customers c LEFT JOIN LATERAL (
          SELECT COUNT(*) AS n FROM orders o1 JOIN orders o2
            ON o1.status = o2.status AND o2.customer_id = o1.customer_id
          WHERE o1.customer_id = c.id) x ON TRUE
        ORDER BY c.name
        """;

    /// <summary>
    /// The refusal, at planning and by name. Before it the statement planned and returned
    /// <c>ACME 4, BRIX 6</c> — a wrong answer with nothing in the plan text or the result to say so.
    /// </summary>
    [Fact]
    public async Task A_lateral_that_ties_one_outer_column_to_two_tables_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();

        var failure = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(SharedOuterColumn).AsTask());

        Assert.Equal(PlanErrorKind.Unsupported, failure.Kind);
        Assert.Contains("LATERAL sub-query that constrains o1 and o2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("same outer column c.id", failure.Message, StringComparison.Ordinal);
        Assert.Contains("o2 against o1", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the way out answers, with the counts derived above: ACME's two orders share a status only
    /// with themselves, BRIX's two share one with each other as well.
    /// </summary>
    [Fact]
    public async Task The_rewritten_form_returns_the_rows_counted_by_hand()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var rows = await RowsAsync(engine, RewrittenThroughO1);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("ACME", 2L), rows[0]);
        Assert.Equal(("BRIX", 4L), rows[1]);

        // The counts the lost equality would have produced. Asserting they are absent is what makes
        // this test fail loudly if the refusal is ever lifted without the rewrite behind it.
        Assert.DoesNotContain(rows, row => row.Item2 is 6L);
    }

    /// <summary>
    /// The neighbour that must keep working: one outer column, one table. It is the shape every
    /// lateral in the corpus has, and the refusal must not reach it.
    /// </summary>
    [Fact]
    public async Task An_ordinary_one_table_lateral_still_answers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateEngineAsync();
        var rows = await RowsAsync(
            engine,
            """
            SELECT c.name, x.n
            FROM customers c LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n FROM orders o WHERE o.customer_id = c.id) x ON TRUE
            ORDER BY c.name
            """);

        Assert.Equal([("ACME", 2L), ("BRIX", 2L)], rows);
    }

    private async Task<ChalkEngine> CreateEngineAsync()
    {
        var source = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("customers", Customers)
            .AddTable("orders", Orders)
            .Build();

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "lateral-correlation",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });
    }

    private static async Task<List<(string Name, long N)>> RowsAsync(ChalkEngine engine, string sql)
    {
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);

        var rows = new List<(string, long)>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(
                    ResultComparer.Rows([batch])
                        .Select(row => (Convert.ToString(row[0])!, Convert.ToInt64(row[1]))));
            }
        }

        return rows;
    }
}
