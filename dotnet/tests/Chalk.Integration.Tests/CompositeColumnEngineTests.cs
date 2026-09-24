using Apache.Arrow;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Composite columns through the engine (D302): a POCO table whose member is a record is read whole,
/// by field, by <c>t.c.*</c> and by <c>SELECT *</c>; filtered, ordered and grouped by a field; read
/// back as the host's record; looked up through an index on another column; and refused by name
/// wherever a composite value itself would be compared, sorted or grouped. Every statement runs
/// through both engines, which must agree.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class CompositeColumnEngineTests(SharedSidecar sidecar)
{
    /// <summary>One side of a quote.</summary>
    public readonly record struct Side(double Price, long Size);

    /// <summary>Where a quote was made: a record class, so the composite may be NULL.</summary>
    public sealed record Venue(Utf8String Name, string? Country);

    public sealed record Quote(long Id, Utf8String Symbol, Side Bid, Side? Ask, Venue? Venue);

    /// <summary>Which desk trades a symbol: what a join carries a composite column to.</summary>
    public sealed record Desk(Utf8String Symbol, string Name);

    private static readonly Desk[] Desks =
    [
        new(System.Text.Encoding.UTF8.GetBytes("BTC"), "crypto"),
        new(System.Text.Encoding.UTF8.GetBytes("ETH"), "crypto-alt"),
    ];

    private static readonly Quote[] Quotes =
    [
        .. Enumerable.Range(0, 60).Select(i => new Quote(
            i,
            System.Text.Encoding.UTF8.GetBytes(i % 3 == 0 ? "BTC" : "ETH"),
            new Side(90 + i, 10 * i),
            i % 4 == 3 ? null : new Side(91 + i, 5 * i),
            i % 5 == 4 ? null : new Venue(System.Text.Encoding.UTF8.GetBytes(i % 2 == 0 ? "north" : "south"), i % 3 == 1 ? null : "NZ"))),
    ];

    private async Task<List<string>> BothAsync(string sql)
    {
        List<string>? first = null;
        foreach (var engine in new[] { ExecutionEngine.Vectorised, ExecutionEngine.Reference })
        {
            var source = new PocoSourceBuilder("mem")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("quotes", Quotes, t => t.OrderedBy(q => q.Id).UniqueKey(q => q.Id).Index(q => q.Symbol))
                .AddTable("desks", Desks, t => t.UniqueKey(d => d.Symbol))
                .Build();
            await using var chalk = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "composite-columns",
                Sources = [source],
                Planner = sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { Engine = engine, BatchSize = 16 },
            });

            var prepared = await chalk.PrepareAsync(sql);
            await using var execution = await chalk.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
            var rows = new List<string>();
            await foreach (var batch in execution.Batches)
            {
                using (batch)
                {
                    rows.AddRange(batch.ToRows().Select(Render));
                }
            }

            if (first is null)
            {
                first = rows;
            }
            else
            {
                Assert.Equal(first, rows);
            }
        }

        return first!;
    }

    private static string Render(object?[] row) => string.Join("|", row.Select(Value));

    private static string Value(object? value) => value switch
    {
        null => "NULL",
        object?[] fields => "[" + string.Join(", ", fields.Select(Value)) + "]",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    [Fact]
    public async Task A_composite_column_reads_whole_by_field_by_star_and_by_select_star()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var whole = await BothAsync("SELECT id, bid, ask, venue FROM quotes ORDER BY id");
        Assert.Equal(Quotes.Length, whole.Count);
        Assert.Equal("3|[93, 30]|NULL|[south, NZ]", whole[3]);
        Assert.Equal("4|[94, 40]|[95, 20]|NULL", whole[4]);

        var fields = await BothAsync(
            "SELECT q.id, q.bid.price AS bid, q.ask.price AS ask, q.venue.country AS country FROM quotes q ORDER BY q.id");
        Assert.Equal("3|93|NULL|NZ", fields[3]);
        Assert.Equal("1|91|92|NULL", fields[1]);

        var star = await BothAsync("SELECT q.id, q.bid.* FROM quotes q ORDER BY q.id");
        Assert.Equal("5|95|50", star[5]);

        var selectStar = await BothAsync("SELECT * FROM quotes ORDER BY id");
        Assert.Equal("2|ETH|[92, 20]|[93, 10]|[north, NZ]", selectStar[2]);
    }

    [Fact]
    public async Task A_field_filters_orders_and_groups_and_the_composite_is_carried_through_a_sort()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var filtered = await BothAsync(
            "SELECT q.id FROM quotes q WHERE q.bid.price > 140 AND q.ask IS NOT NULL ORDER BY q.id");
        Assert.Equal(
            Quotes.Where(q => q.Bid.Price > 140 && q.Ask is not null).Select(q => q.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            filtered);

        var ordered = await BothAsync(
            "SELECT q.id, q.ask FROM quotes q WHERE q.ask IS NOT NULL ORDER BY q.ask.price DESC LIMIT 3");
        Assert.Equal(["58|[149, 290]", "57|[148, 285]", "56|[147, 280]"], ordered);

        var grouped = await BothAsync(
            "SELECT q.venue.name AS venue, COUNT(*) AS n FROM quotes q GROUP BY q.venue.name ORDER BY venue");
        Assert.Equal(["north|24", "south|24", "NULL|12"], grouped);

        var looked = await BothAsync("SELECT q.id, q.venue FROM quotes q WHERE q.symbol = 'BTC' ORDER BY q.id LIMIT 2");
        Assert.Equal(["0|[north, NZ]", "3|[south, NZ]"], looked);

        // A join carries the composite column whole from either side it is on.
        var joined = await BothAsync(
            "SELECT q.id, d.name AS desk, q.venue, q.ask FROM desks d JOIN quotes q ON q.symbol = d.symbol "
            + "WHERE q.id < 5 ORDER BY q.id");
        Assert.Equal(
            [
                "0|crypto|[north, NZ]|[91, 0]",
                "1|crypto-alt|[south, NULL]|[92, 5]",
                "2|crypto-alt|[north, NZ]|[93, 10]",
                "3|crypto|[south, NZ]|NULL",
                "4|crypto-alt|NULL|[95, 20]",
            ],
            joined);
    }

    [Fact]
    public async Task A_composite_column_reads_back_as_the_host_s_record()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem").AddTable("quotes", Quotes).Build();
        await using var chalk = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "composite-columns-read-back",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });
        var prepared = await chalk.PrepareAsync("SELECT Id, Ask, Venue FROM quotes ORDER BY Id");
        await using var execution = await chalk.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var row = 0;
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++, row++)
                {
                    Assert.Equal(Quotes[row].Ask, batch.Column(1).GetComposite<Side?>(i));
                    Assert.Equal(Quotes[row].Venue, batch.Column(2).GetComposite<Venue>(i));
                }
            }
        }

        Assert.Equal(Quotes.Length, row);
    }

    [Theory]
    [InlineData("SELECT id FROM quotes ORDER BY bid", "ORDER BY a composite value (bid)")]
    [InlineData("SELECT q.id FROM quotes q WHERE q.bid = q.ask", "a comparison of a composite value (q.bid = q.ask)")]
    [InlineData("SELECT bid, COUNT(*) FROM quotes GROUP BY bid", "GROUP BY a composite value (bid)")]
    [InlineData("SELECT DISTINCT venue FROM quotes", "SELECT DISTINCT over the composite column 'venue'")]
    public async Task A_composite_column_is_compared_sorted_or_grouped_nowhere(string sql, string refusal)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("quotes", Quotes)
            .Build();
        await using var chalk = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "composite-columns-refused",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => chalk.PrepareAsync(sql).AsTask());
        Assert.Contains(refusal, error.Message, StringComparison.Ordinal);
    }
}
