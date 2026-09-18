namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The typed table handles of D271 (h) (<c>docs/design/44-catalog-registration.md</c> §6 (h)): the
/// source carried with the name, so two sources holding a table of one name over one row type cannot
/// be confused, and the row type carried with both.
/// </summary>
/// <remarks>
/// The confusion this prevents is silent without a handle: a refresh that names the right table on
/// the wrong source succeeds, because the only check either source can make compares row types and
/// the two agree. So the assertion is on <em>which source's rows moved</em>, which is what a wrong
/// answer would look like from the outside.
/// </remarks>
public sealed class PocoTableHandleTests
{
    private sealed record Order(int Id, string Region);

    private static readonly Order[] Left = [new(1, "north"), new(2, "north")];
    private static readonly Order[] Right = [new(3, "south")];

    [Fact]
    public void A_handle_carries_the_source_the_schema_and_the_name()
    {
        var source = new PocoSourceBuilder("left", "l")
            .AddTable("orders", Left, out var orders)
            .Build();

        Assert.Equal("orders", orders.Name);
        Assert.Equal("left", orders.SourceId);
        Assert.Equal("l", orders.Schema);
        Assert.Same(source, orders.Runtime);
        Assert.Equal("l.orders", orders.ToString());
    }

    [Fact]
    public void The_same_table_looked_up_twice_is_the_same_handle()
    {
        var source = new PocoSourceBuilder("left", "l")
            .AddTable("orders", Left, out var atRegistration)
            .Build();

        var lookedUp = source.Table<Order>("orders");

        Assert.Equal(atRegistration, lookedUp);
        Assert.Same(atRegistration.Runtime, lookedUp.Runtime);
    }

    /// <summary>
    /// The lookup makes the binding on first call, and several threads may make that call at once
    /// (F94): a host that hands each of its workers a handle at start-up does, and before this the
    /// dictionary was read and written with no guard at all. What corrupts is the dictionary rather
    /// than the entry, so the assertion is that every caller came back with the <em>one</em> handle
    /// D271 (h) promises and that none of them threw.
    /// </summary>
    /// <remarks>
    /// No timing assertion, and none of its shape: the threads are released together by a barrier
    /// so the calls overlap, and what is read afterwards is the set of handles they returned.
    /// </remarks>
    [Fact]
    public void A_handle_asked_for_from_several_threads_at_once_is_made_once()
    {
        const int Threads = 8;
        const int Rounds = 64;

        // One fresh source per round, so every round starts from an empty dictionary: the race is
        // the *first* call for a name, and a source that has already made its handles cannot lose.
        var sources = new PocoSource[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            sources[round] = new PocoSourceBuilder("left", "l")
                .AddTable("orders", Left)
                .AddTable("lines", Right)
                .Build();
        }

        using var start = new Barrier(Threads);
        var handles = new PocoTable<Order>[Rounds, Threads];
        var failures = new Exception?[Threads];
        var workers = new Thread[Threads];
        for (var i = 0; i < Threads; i++)
        {
            var slot = i;
            workers[slot] = new Thread(() =>
            {
                try
                {
                    for (var round = 0; round < Rounds; round++)
                    {
                        start.SignalAndWait();
                        handles[round, slot] =
                            sources[round].Table<Order>(slot % 2 == 0 ? "orders" : "lines");
                    }
                }
                catch (Exception failed)
                {
                    failures[slot] = failed;
                    start.RemoveParticipant();
                }
            });
            workers[slot].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        Assert.All(failures, Assert.Null);
        for (var round = 0; round < Rounds; round++)
        {
            var made = new Dictionary<string, PocoTable<Order>>(StringComparer.Ordinal);
            for (var slot = 0; slot < Threads; slot++)
            {
                var handle = handles[round, slot];
                Assert.Equal(slot % 2 == 0 ? "orders" : "lines", handle.Name);
                if (made.TryGetValue(handle.Name, out var first))
                {
                    Assert.Equal(first, handle);
                }
                else
                {
                    made[handle.Name] = handle;
                }
            }

            Assert.Equal(2, made.Count);
        }
    }

    /// <summary>
    /// Two sources, one table name, one row type: a replacement through one handle never touches the
    /// other. Without the handle both calls would be <c>Replace(source, "orders", rows)</c> and the
    /// wrong <c>source</c> would be accepted in silence.
    /// </summary>
    [Fact]
    public async Task A_replacement_through_one_handle_never_touches_the_other_source()
    {
        var left = new PocoSourceBuilder("left", "l")
            .AddTable("orders", Left, out var leftOrders)
            .Build();
        var right = new PocoSourceBuilder("right", "r")
            .AddTable("orders", Right, out var rightOrders)
            .Build();

        Assert.Equal(2, RowCount(left));
        Assert.Equal(1, RowCount(right));

        // Through the left handle only. The handle's own source is what the replacement reaches,
        // which is the whole point: nothing in the call repeats it.
        Assert.Same(left, leftOrders.Runtime);
        await Replace(leftOrders, [new Order(9, "west")]);

        Assert.Equal(1, RowCount(left));
        Assert.Equal(1, RowCount(right));
        Assert.Equal("west", (await RegionsAsync(left)).Single());
        Assert.Equal("south", (await RegionsAsync(right)).Single());

        // And through the right one.
        await Replace(rightOrders, [new Order(7, "east"), new Order(8, "east")]);

        Assert.Equal(1, RowCount(left));
        Assert.Equal(2, RowCount(right));
        Assert.Equal("west", (await RegionsAsync(left)).Single());
        Assert.Equal(["east", "east"], await RegionsAsync(right));
    }

    /// <summary>
    /// And the scoped refresh the same way: a table target re-reads exactly one table of exactly one
    /// source. The registrations are <c>Func</c>s over a swappable field, so what the assertion sees
    /// is which source went and looked again.
    /// </summary>
    [Fact]
    public async Task A_scoped_refresh_through_one_handle_never_touches_the_other_source()
    {
        IReadOnlyList<Order> leftRows = Left;
        IReadOnlyList<Order> rightRows = Right;
        var left = new PocoSourceBuilder("left", "l")
            .AddTable("orders", () => leftRows, out var leftOrders)
            .Build();
        var right = new PocoSourceBuilder("right", "r")
            .AddTable("orders", () => rightRows, out _)
            .Build();

        leftRows = [new Order(9, "west")];
        rightRows = [new Order(7, "east"), new Order(8, "east")];

        await leftOrders.Runtime.RefreshTableAsync(leftOrders.Name, TestContext.Current.CancellationToken);

        Assert.Equal(1, RowCount(left));
        Assert.Equal(1, RowCount(right));
        Assert.Equal("west", (await RegionsAsync(left)).Single());
        Assert.Equal("south", (await RegionsAsync(right)).Single());
    }

    /// <summary>
    /// The string form is what the handle replaces, and it is the one that can be got wrong: the row
    /// types agree, so the only thing left to refuse is the table's absence. This asserts that
    /// refusal, which is the runtime half of the check the handle makes at compile time.
    /// </summary>
    [Fact]
    public void The_string_form_refuses_a_table_the_source_does_not_have()
    {
        var left = new PocoSourceBuilder("left", "l")
            .AddTable("orders", Left)
            .Build();

        var refused = Assert.Throws<SourceContractException>(
            () => left.ValidateRefresh(
            [
                new SourceRefreshEntry
                {
                    Table = "notes",
                    Kind = SourceRefreshKind.Replace,
                    RowType = typeof(Order),
                    Rows = (IReadOnlyList<Order>)Left,
                },
            ]));

        Assert.Contains("notes", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A handle asked for over the wrong row type is refused, naming both.</summary>
    [Fact]
    public void A_handle_over_the_wrong_row_type_is_refused()
    {
        var left = new PocoSourceBuilder("left", "l")
            .AddTable("orders", Left)
            .Build();

        var refused = Assert.Throws<ArgumentException>(() => left.Table<string>("orders"));

        Assert.Contains("Order", refused.Message, StringComparison.Ordinal);
        Assert.Contains("String", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A default handle names nothing and says so rather than resolving to something.</summary>
    [Fact]
    public void A_default_handle_names_nothing()
    {
        PocoTable<Order> none = default;

        Assert.Equal("", none.Name);
        Assert.Throws<InvalidOperationException>(() => none.Runtime);
    }

    /// <summary>
    /// What <c>RefreshBuilder.Replace(handle, rows)</c> does, one source at a time: the handle names
    /// the source, so nothing here repeats it. (The builder itself lives in the client package, which
    /// this one does not reference; what it adds over this is the transaction.)
    /// </summary>
    private static async Task Replace(PocoTable<Order> table, IReadOnlyList<Order> rows)
    {
        var source = (IRefreshableSource)table.Runtime;
        var entry = new SourceRefreshEntry
        {
            Table = table.Name,
            Kind = SourceRefreshKind.Replace,
            RowType = typeof(Order),
            Rows = rows,
        };
        var commit = await source.PrepareRefreshAsync([entry], TestContext.Current.CancellationToken);
        commit.Commit();
    }

    private static long RowCount(PocoSource source) =>
        source.DescribeSchema().Tables.Single(t => t.Name == "orders").RowCount;

    private static async Task<string[]> RegionsAsync(PocoSource source)
    {
        var values = await PocoTestSupport.ColumnAsync(source, "orders", column: 1, batchSize: 16);
        return [.. values.Select(v => v?.ToString() ?? "")];
    }
}
