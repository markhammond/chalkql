using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a standing view refuses when it is made (D339, <c>docs/design/64-incremental-views.md</c> §6),
/// each refusal as a host meets it — its code and what it says to do — and what it admits beside it.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StandingViewCheckTests(SharedSidecar sidecar) : IDisposable
{
    private sealed record Trade(int Seq, int Book, double Price, decimal Qty, DateTime Ts);

    private struct Key
    {
        public long Book;
    }

    private struct Row
    {
        public long Value;
    }

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    // ---- floating point ----

    /// <summary>
    /// A SUM over DOUBLE accumulates in floating point, so it is refused, naming the column and saying
    /// how to stay exact or to opt in; opted in, it is admitted.
    /// </summary>
    [Fact]
    public async Task A_floating_point_sum_is_refused_unless_the_view_opts_in()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();
        const string Notional = "SELECT book, SUM(price) AS notional FROM trades GROUP BY book";

        var refused = await RefusedAsync(engine, Notional);
        Assert.Equal(ChalkErrorCodes.InexactAccumulation, refused.Code);
        Assert.Contains("column 'notional', computed by a SUM over a DOUBLE value", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Cast the value to DECIMAL", refused.Message, StringComparison.Ordinal);

        await using var view = await engine.MaterializeAsync(Notional, Options(ViewExactness.EqualUpToRounding));
        Assert.Equal(2, view.Count);
    }

    /// <summary>An AVG over DOUBLE accumulates too, and a SUM over a window does.</summary>
    [Theory]
    [InlineData("SELECT book, AVG(price) AS mean FROM trades GROUP BY book")]
    [InlineData("SELECT seq AS book, SUM(price) OVER (ORDER BY seq) AS so_far FROM trades")]
    public async Task Every_floating_point_accumulation_is_refused(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();

        Assert.Equal(ChalkErrorCodes.InexactAccumulation, (await RefusedAsync(engine, sql)).Code);
    }

    /// <summary>
    /// COUNT, MIN and MAX over DOUBLE do no arithmetic, and a sum over DECIMAL is exact: each is
    /// admitted without an opt-in.
    /// </summary>
    [Theory]
    [InlineData("SELECT book, COUNT(price) AS n FROM trades GROUP BY book")]
    [InlineData("SELECT book, CAST(MAX(price) AS BIGINT) AS top FROM trades GROUP BY book")]
    [InlineData("SELECT book, CAST(SUM(qty) AS BIGINT) AS qty FROM trades GROUP BY book")]
    [InlineData("SELECT book, CAST(SUM(CAST(price AS DECIMAL(18, 4))) AS BIGINT) AS notional FROM trades GROUP BY book")]
    public async Task Exact_columns_need_no_opt_in(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();

        await using var view = await engine.MaterializeAsync(sql, Options());
        Assert.Equal(2, view.Count);
    }

    // ---- functions ----

    /// <summary>A host function declared STABLE or VOLATILE is refused, naming it and its volatility.</summary>
    [Theory]
    [InlineData("stable_lots", "STABLE")]
    [InlineData("volatile_lots", "VOLATILE")]
    public async Task A_host_function_that_is_not_immutable_is_refused(string function, string volatility)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();

        var refused = await RefusedAsync(engine, $"SELECT book, SUM({function}(seq)) AS lots FROM trades GROUP BY book");

        Assert.Equal(ChalkErrorCodes.NondeterministicFunction, refused.Code);
        Assert.Contains($"{function}, a host function declared {volatility}", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host function that said nothing is immutable by default and admitted, unless the view asks
    /// for an explicit declaration; one registered with Immutable() is admitted either way.
    /// </summary>
    [Fact]
    public async Task An_explicit_declaration_is_asked_for_only_by_the_view_that_wants_one()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();
        const string Default = "SELECT book, SUM(default_lots(seq)) AS lots FROM trades GROUP BY book";
        const string Declared = "SELECT book, SUM(pure_lots(seq)) AS lots FROM trades GROUP BY book";

        await using (await engine.MaterializeAsync(Default, Options()))
        {
        }

        var refused = await RefusedAsync(engine, Default, Options(requireExplicitImmutable: true));
        Assert.Equal(ChalkErrorCodes.NondeterministicFunction, refused.Code);
        Assert.Contains("does not say Immutable()", refused.Message, StringComparison.Ordinal);

        await using var view = await engine.MaterializeAsync(Declared, Options(requireExplicitImmutable: true));
        Assert.Equal(2, view.Count);
    }

    // ---- the clock ----

    /// <summary>On an engine without a clock, a view that reads the current time is refused when made.</summary>
    [Fact]
    public async Task The_current_time_needs_a_clock()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(clock: null);

        var refused = await RefusedAsync(
            engine, "SELECT book, COUNT(*) AS n FROM trades WHERE ts <= CURRENT_TIMESTAMP GROUP BY book");

        Assert.Equal(ChalkErrorCodes.ClockRequired, refused.Code);
        Assert.Contains("Name one with ChalkEngineOptions.Clock", refused.Message, StringComparison.Ordinal);
    }

    // ---- order ----

    /// <summary>
    /// A keyed result has no order: an ORDER BY without a LIMIT is refused, as is a LIMIT the input's
    /// order would choose rows for. A LIMIT ordered by the key is admitted.
    /// </summary>
    [Theory]
    [InlineData("SELECT book, COUNT(*) AS n FROM trades GROUP BY book ORDER BY book", "with no LIMIT")]
    [InlineData("SELECT book, COUNT(*) AS n FROM trades GROUP BY book LIMIT 1", "with no ORDER BY")]
    [InlineData("SELECT book, COUNT(*) AS n FROM trades GROUP BY book ORDER BY n LIMIT 1", "does not order by the view's key")]
    public async Task An_order_a_keyed_result_cannot_keep_is_refused(string sql, string says)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();

        var refused = await RefusedAsync(engine, sql);

        Assert.Equal(ChalkErrorCodes.UnsupportedInView, refused.Code);
        Assert.Contains(says, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_limit_ordered_by_the_key_is_admitted()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync();

        await using var view = await engine.MaterializeAsync(
            "SELECT book, COUNT(*) AS n FROM trades GROUP BY book ORDER BY book LIMIT 1", Options());

        Assert.Equal(1, view.Count);
    }

    // ---- sources ----

    /// <summary>
    /// A source over a database changes outside the engine's refreshes, so a view over it is refused,
    /// saying to load what the view needs into an in-process table.
    /// </summary>
    [Fact]
    public async Task A_database_source_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [Books()],
            Planner = sidecar.CreatePlanner(),
        }, TestContext.Current.CancellationToken);

        var refused = await RefusedAsync(engine, "SELECT book, COUNT(*) AS n FROM books GROUP BY book");

        Assert.Equal(ChalkErrorCodes.SourceOutsideEpochs, refused.Code);
        Assert.Contains("source 'db'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Load what the view needs into an in-process table", refused.Message, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

    private static ViewOptions<Key, Row> Options(
        ViewExactness exactness = ViewExactness.Exact, bool requireExplicitImmutable = false) => new()
    {
        Key = ["book"],
        Map = static (in ViewRow row, out Key key, out Row value) =>
        {
            key = new Key { Book = row.GetInt64(0) };
            value = new Row { Value = row.GetValue(1) is { } cell ? Convert.ToInt64(cell, System.Globalization.CultureInfo.InvariantCulture) : 0 };
        },
        Exactness = exactness,
        RequireExplicitImmutable = requireExplicitImmutable,
    };

    private static async Task<StandingViewException> RefusedAsync(
        ChalkEngine engine, string sql, ViewOptions<Key, Row>? options = null) =>
        await Assert.ThrowsAsync<StandingViewException>(async () =>
            await engine.MaterializeAsync(sql, options ?? Options(), TestContext.Current.CancellationToken));

    private async Task<ChalkEngine> EngineAsync(string? clock = "now")
    {
        var desk = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "trades",
                (IReadOnlyList<Trade>)
                [
                    new(1, 1, 1.5, 2m, Open),
                    new(2, 2, 2.5, 3m, Open.AddSeconds(1)),
                    new(3, 1, 3.5, 4m, Open.AddSeconds(2)),
                ],
                t => t.AppendOnly(x => x.Ts))
            .AddFunction("default_lots", f => f.Scalar<int, long>("seq").Strict().Client())
            .AddFunction("pure_lots", f => f.Scalar<int, long>("seq").Strict().Immutable().Client())
            .AddFunction("stable_lots", f => f.Scalar<int, long>("seq").Strict().Stable().Client())
            .AddFunction("volatile_lots", f => f.Scalar<int, long>("seq").Strict().Volatile().Client())
            .Build();

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [desk],
            Planner = sidecar.CreatePlanner(),
            Clock = clock,
            Functions = registry =>
            {
                registry.AddScalar<int, long>("default_lots", static seq => seq * 10L);
                registry.AddScalar<int, long>("pure_lots", static seq => seq * 10L);
                registry.AddScalar<int, long>("stable_lots", static seq => seq * 10L);
                registry.AddScalar<int, long>("volatile_lots", static seq => seq * 10L);
            },
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>A table of books in DuckDB, declared rather than discovered.</summary>
    private ISourceRuntime Books()
    {
        var file = DuckDbFile.Create("standing-view-checks");
        _owned.Add(file);
        using (var connection = new DuckDBConnection(file.ConnectionString))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE \"books\" (\"book\" INTEGER NOT NULL PRIMARY KEY)");
            Execute(connection, "INSERT INTO \"books\" VALUES (1), (2)");
        }

        return new AdoSourceBuilder("db", () => new DuckDBConnection(file.ConnectionString), "db")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable("books", [new ColumnDescriptor { Name = "book", Type = ChalkType.Int32() }])
            .Build();
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
