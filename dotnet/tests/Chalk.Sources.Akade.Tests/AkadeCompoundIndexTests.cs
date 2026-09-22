using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Chalk.Tests;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D280 — a compound Akade key, in both the spellings a host can write it, and the adapter that
/// serves it.
/// </summary>
public sealed class AkadeCompoundIndexTests
{
    private sealed record Trade(int Id, string Symbol, long Ts, decimal Price);

    private static Trade[] Rows() =>
    [
        new(1, "ETH", 300, 10m),
        new(2, "BTC", 100, 20m),
        new(3, "BTC", 300, 30m),
        new(4, "ETH", 100, 40m),
        new(5, "BTC", 200, 50m),
        new(6, "ETH", 200, 60m),
        new(7, "ADA", 100, 70m),
    ];

    /// <summary>
    /// The method spelling. Static, so Akade files the index under this text and the method identity
    /// is the same one the host declares.
    /// </summary>
    private static class TradeKeys
    {
        public static (string Symbol, long Ts) SymbolAndTs(Trade x) => (x.Symbol, x.Ts);

        public static (long Ts, decimal Price) TsAndPrice(Trade x) => (x.Ts, x.Price);
    }

    private static IndexedSet<int, Trade> Build(IEnumerable<Trade> rows) =>
        rows.ToIndexedSet(x => x.Id)
            .WithIndex(x => (x.Symbol, x.Ts))
            .WithRangeIndex(x => (x.Ts, x.Price))
            .WithIndex(TradeKeys.SymbolAndTs)
            .WithRangeIndex(TradeKeys.TsAndPrice)
            .Build();

    private static IndexedSetSource<Trade> Source(
        Action<IndexedSetSourceBuilder<Trade>>? configure = null)
    {
        var builder = AkadeSource
            .From("trades", Build(Rows()))
            .TableName("trades")
            .NamingPolicy(PocoNamingPolicy.SnakeCase);

        configure?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public void A_lambda_tuple_accessor_names_its_own_columns()
    {
        var indexes = Source().DescribeSchema().Tables.Single().Indexes
            .ToDictionary(index => index.Name, StringComparer.Ordinal);

        var hash = indexes["x => (x.Symbol, x.Ts)"];
        Assert.Equal(IndexKind.Hash, hash.Kind);
        Assert.Equal([1, 2], hash.Columns);
        Assert.False(hash.Unique);

        var ordered = indexes["x => (x.Ts, x.Price)"];
        Assert.Equal(IndexKind.Ordered, ordered.Kind);
        Assert.Equal([2, 3], ordered.Columns);
        Assert.Equal(
            [SortDirection.AscNullsLast, SortDirection.AscNullsLast],
            ordered.Directions);
    }

    /// <summary>
    /// A method accessor's text names no columns, so it stays undisclosed until the host says what
    /// the components are — which is what keeps a computed key out of the catalog.
    /// </summary>
    [Fact]
    public void A_method_accessor_is_undisclosed_until_the_host_declares_its_components()
    {
        var undeclared = Source().DescribeSchema().Tables.Single().Indexes;
        Assert.DoesNotContain(
            undeclared,
            index => index.Name.Contains("SymbolAndTs", StringComparison.Ordinal));

        var declared = Source(b => b
                .CompoundIndex(TradeKeys.SymbolAndTs, x => x.Symbol, x => x.Ts)
                .CompoundIndex(TradeKeys.TsAndPrice, x => x.Ts, x => x.Price))
            .DescribeSchema()
            .Tables
            .Single()
            .Indexes
            .ToDictionary(index => index.Name, StringComparer.Ordinal);

        Assert.Equal(IndexKind.Hash, declared["TradeKeys.SymbolAndTs"].Kind);
        Assert.Equal([1, 2], declared["TradeKeys.SymbolAndTs"].Columns);
        Assert.Equal(IndexKind.Ordered, declared["TradeKeys.TsAndPrice"].Kind);
        Assert.Equal([2, 3], declared["TradeKeys.TsAndPrice"].Columns);
    }

    [Fact]
    public void A_compound_declaration_of_the_wrong_arity_is_refused_by_name()
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            AkadeSource
                .From("trades", Build(Rows()))
                .CompoundIndex(TradeKeys.SymbolAndTs, x => x.Symbol, x => x.Ts, x => x.Price));

        Assert.Contains("TradeKeys.SymbolAndTs", failure.Message, StringComparison.Ordinal);
        Assert.Contains("2 component(s) but 3 member(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_compound_declaration_of_the_wrong_types_is_refused_by_name()
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            AkadeSource
                .From("trades", Build(Rows()))
                .CompoundIndex(TradeKeys.SymbolAndTs, x => x.Ts, x => x.Symbol));

        Assert.Contains("TradeKeys.SymbolAndTs", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'String' at component 1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'Ts'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A key that is not a tuple at all is not a compound key.</summary>
    [Fact]
    public void A_scalar_accessor_declared_as_a_compound_key_is_refused_by_name()
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            AkadeSource
                .From("trades", Build(Rows()))
                .CompoundIndex<long>(x => x.Ts, x => x.Ts));

        Assert.Contains(
            "not a ValueTuple of two to four components",
            failure.Message,
            StringComparison.Ordinal);
    }

    private static IndexedSet<Trade> Plain(IEnumerable<Trade> rows) =>
        rows.ToIndexedSet()
            .WithIndex(x => (x.Symbol, x.Ts))
            .WithRangeIndex(x => (x.Ts, x.Price))
            .Build();

    private static AkadeTupleIndex<Trade, (string Symbol, long Ts)> HashIndex(IndexedSet<Trade> set) =>
        new(
            new IndexDescriptor
            {
                Name = "ix_trades_symbol_ts_hash",
                Kind = IndexKind.Hash,
                Columns = [1, 2],
            },
            set,
            x => (x.Symbol, x.Ts),
            AkadeTupleKey<(string Symbol, long Ts)>.Create(
                [AkadeKeyOrder.Ascending<string>()!, AkadeKeyOrder.Ascending<long>()!]),
            "x => (x.Symbol, x.Ts)",
            "trades",
            "trades");

    private static AkadeTupleIndex<Trade, (long Ts, decimal Price)> RangeIndex(IndexedSet<Trade> set) =>
        new(
            new IndexDescriptor
            {
                Name = "ix_trades_ts_price",
                Kind = IndexKind.Ordered,
                Columns = [2, 3],
                Directions = [SortDirection.AscNullsLast, SortDirection.AscNullsLast],
            },
            set,
            x => (x.Ts, x.Price),
            AkadeTupleKey<(long Ts, decimal Price)>.Create(
                [AkadeKeyOrder.Ascending<long>()!, AkadeKeyOrder.Ascending<decimal>()!]),
            "x => (x.Ts, x.Price)",
            "trades",
            "trades");

    [Fact]
    public void A_compound_hash_index_agrees_with_a_full_scan()
    {
        var rows = Rows();
        var report = PocoIndexConformance.Verify(
            HashIndex(Plain(rows)),
            rows,
            [x => x.Symbol, x => x.Ts]);

        Assert.True(report.Ranges >= 8, report.ToString());
    }

    [Fact]
    public void A_compound_range_index_agrees_with_a_full_scan()
    {
        var rows = Rows();
        var report = PocoIndexConformance.Verify(
            RangeIndex(Plain(rows)),
            rows,
            [x => x.Ts, x => x.Price]);

        Assert.True(report.Ranges >= 20, report.ToString());
    }

    /// <summary>
    /// The shape D37 lets an ORDERED index serve: an equality prefix and one range on the last bound
    /// column. The generated battery does not produce it, so it is stated here.
    /// </summary>
    [Fact]
    public void A_compound_range_index_serves_an_equality_prefix_with_a_range_on_the_last_column()
    {
        var rows = Rows();
        var report = PocoIndexConformance.Verify(
            RangeIndex(Plain(rows)),
            rows,
            [x => x.Ts, x => x.Price],
            [
                new IndexKeyRange { Lower = [100L, 40m], Upper = [100L] },
                new IndexKeyRange { Lower = [100L, 40m], LowerInclusive = false, Upper = [100L] },
                new IndexKeyRange { Lower = [100L], Upper = [100L, 40m] },
                new IndexKeyRange { Lower = [100L], Upper = [100L, 40m], UpperInclusive = false },
                new IndexKeyRange { Lower = [100L, 20m], Upper = [100L, 70m] },
                new IndexKeyRange { Lower = [300L, 10m], Upper = [300L, 30m], LowerInclusive = false },
            ]);

        Assert.Equal(6, report.Ranges);
    }

    /// <summary>
    /// The prefix-fill rule, which is the one thing a tuple key has to get right: an exclusive bound
    /// on a prefix excludes every key with that prefix, so the component it does not reach takes the
    /// <em>maximum</em>.
    /// </summary>
    [Fact]
    public void An_exclusive_prefix_bound_excludes_the_whole_prefix()
    {
        var index = RangeIndex(Plain(Rows()));

        var inclusive = index.Lookup(new IndexKeyRange
        {
            Lower = [200L],
            LowerInclusive = true,
            Upper = [],
        });

        var exclusive = index.Lookup(new IndexKeyRange
        {
            Lower = [200L],
            LowerInclusive = false,
            Upper = [],
        });

        Assert.Equal([200L, 200L, 300L, 300L], inclusive.Select(t => t.Ts));
        Assert.Equal([300L, 300L], exclusive.Select(t => t.Ts));
    }

    [Fact]
    public void A_compound_hash_index_refuses_anything_but_the_whole_key_by_name()
    {
        var index = HashIndex(Plain(Rows()));

        var failure = Assert.Throws<SourceContractException>(
            () => index.Lookup(IndexKeyRange.Equality("BTC")).ToList());

        Assert.Contains("ix_trades_symbol_ts_hash", failure.Message, StringComparison.Ordinal);
        Assert.Contains("equality on all 2 key column(s)", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hash index is a dictionary of keys and knows how many it holds; the ordered one does not,
    /// and says so rather than estimating, because the planner divides by this.
    /// </summary>
    [Fact]
    public void The_distinct_key_count_is_exact_or_absent()
    {
        var rows = Rows();
        Assert.Equal(7, HashIndex(Plain(rows)).DistinctCount(2));
        Assert.Null(HashIndex(Plain(rows)).DistinctCount(1));
        Assert.Null(RangeIndex(Plain(rows)).DistinctCount(2));
    }

    /// <summary>
    /// The gate: a tuple lookup allocates for the enumeration and not for the rows. The composed
    /// comparer reads the tuple's fields and calls each component's own comparer, so the guard's one
    /// comparison per row boxes nothing.
    /// </summary>
    [Fact]
    public async Task A_compound_ordered_lookup_allocates_nothing_per_row()
    {
        var many = new List<Trade>(1024);
        for (var i = 0; i < 1024; i++)
        {
            many.Add(new Trade(i, "AAA", i, i));
        }

        var index = RangeIndex(Plain(many));

        var (whole, wholeRows) = await AllocationProbe.SteadyStateAsync(
            () => Task.FromResult(Count(index.Lookup(IndexKeyRange.All))));
        var (few, fewRows) = await AllocationProbe.SteadyStateAsync(
            () => Task.FromResult(Count(index.Lookup(new IndexKeyRange
            {
                Lower = [1016L],
                LowerInclusive = true,
                Upper = [],
            }))));

        Assert.Equal(1024, wholeRows);
        Assert.Equal(8, fewRows);
        Assert.True(
            whole <= few + 256,
            $"{whole} bytes for {wholeRows} rows against {few} for {fewRows}: the difference is "
            + "per-row allocation.");
    }

    private static int Count(IEnumerable<Trade> rows)
    {
        var seen = 0;
        foreach (var row in rows)
        {
            seen += row.Id == int.MinValue ? 0 : 1;
        }

        return seen;
    }
}
