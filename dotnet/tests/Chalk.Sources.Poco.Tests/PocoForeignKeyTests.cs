using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// Declared foreign keys (F14, F16): what the builder resolves, what it refuses, and what
/// <c>verify: true</c> checks against the data. Every part of the shape is checked before the
/// planner ever sees it; the claim itself is checked only when the declaration asks for it.
/// </summary>
public sealed class PocoForeignKeyTests
{
    private sealed record Symbol(string Ticker, string Venue);

    private sealed record Bar(string Ticker, long Volume);

    private static readonly Symbol[] Symbols = [new("BTCUSDT", "binance"), new("ETHUSDT", "binance")];

    private static readonly Bar[] Bars = [new("BTCUSDT", 1), new("ETHUSDT", 2), new("BTCUSDT", 3)];

    private static PocoSourceBuilder Source() => new PocoSourceBuilder("mem")
        .AddTable("symbols", Symbols, t => t.UniqueKey(s => s.Ticker));

    private static TableDescriptor Describe(PocoSource source, string table) =>
        source.DescribeSchema().Tables.Single(
            t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_typed_foreign_key_resolves_to_column_indexes()
    {
        var source = Source()
            .AddTable("bars", Bars, t => t.ForeignKey<Symbol>(b => b.Ticker, to: "symbols", s => s.Ticker, verify: false))
            .Build();

        var key = Assert.Single(Describe(source, "bars").ForeignKeys);
        Assert.Equal([0], key.Columns);
        Assert.Equal("symbols", key.ParentTable);
        Assert.Equal([0], key.ParentColumns);
        Assert.NotEmpty(key.Name);
    }

    [Fact]
    public void A_named_parent_column_resolves_the_same_way()
    {
        var source = Source()
            .AddTable("bars", Bars, t => t.ForeignKey(b => b.Ticker, to: "SYMBOLS", parentColumn: "TICKER", verify: false))
            .Build();

        var key = Assert.Single(Describe(source, "bars").ForeignKeys);
        Assert.Equal([0], key.Columns);
        Assert.Equal([0], key.ParentColumns);
    }

    [Fact]
    public void The_parent_may_be_registered_after_the_child()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("bars", Bars, t => t.ForeignKey<Symbol>(b => b.Ticker, to: "symbols", s => s.Ticker, verify: false))
            .AddTable("symbols", Symbols, t => t.UniqueKey(s => s.Ticker))
            .Build();

        Assert.Single(Describe(source, "bars").ForeignKeys);
    }

    [Fact]
    public void An_unregistered_parent_table_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(() => Source()
            .AddTable("bars", Bars, t => t.ForeignKey(b => b.Ticker, to: "tickers", parentColumn: "ticker", verify: false))
            .Build());

        Assert.Contains("tickers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_parent_column_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(() => Source()
            .AddTable("bars", Bars, t => t.ForeignKey(b => b.Ticker, to: "symbols", parentColumn: "nope", verify: false))
            .Build());

        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_whose_kinds_disagree_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(() => Source()
            .AddTable("bars", Bars, t => t.ForeignKey(b => b.Volume, to: "symbols", parentColumn: "ticker", verify: false))
            .Build());

        Assert.Contains("same kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mismatched_key_arities_are_refused_at_the_call()
    {
        Assert.Throws<ArgumentException>(() => new PocoTableBuilderProbe().Mismatched());
    }

    [Fact]
    public void A_foreign_key_survives_the_wire_round_trip()
    {
        var source = Source()
            .AddTable("bars", Bars, t => t.ForeignKey<Symbol>(b => b.Ticker, to: "symbols", s => s.Ticker, verify: false))
            .Build();
        var catalog = new CatalogContext
        {
            ContextId = "demo",
            Epoch = 1,
            Schemas = [source.DescribeSchema()],
        };

        var message = CatalogSerialization.ToProto(catalog);
        var back = CatalogSerialization.FromProto(
            Chalk.Ir.CatalogContext.Parser.ParseFrom(
                Google.Protobuf.MessageExtensions.ToByteArray(message)));

        var key = Assert.Single(back.Schemas[0].FindTable("bars")!.ForeignKeys);
        Assert.Equal([0], key.Columns);
        Assert.Equal("symbols", key.ParentTable);
        Assert.Equal([0], key.ParentColumns);
    }

    /// <summary>The arity check happens at the call, before anything is registered.</summary>
    private sealed class PocoTableBuilderProbe
    {
        public void Mismatched() => new PocoSourceBuilder("mem")
            .AddTable("bars", Bars, t => t.ForeignKey(
                [b => b.Ticker], to: "symbols", parentColumns: ["ticker", "venue"], verify: false));
    }

    [Fact]
    public void The_fluent_spelling_finds_the_parent_by_row_type()
    {
        var source = Source()
            .AddTable("bars", Bars, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>(s => s.Ticker, verify: true))
            .Build();

        var key = Assert.Single(Describe(source, "bars").ForeignKeys);
        Assert.Equal([0], key.Columns);
        Assert.Equal("symbols", key.ParentTable);
        Assert.Equal([0], key.ParentColumns);
    }

    [Fact]
    public void The_fluent_spelling_accepts_an_explicit_table_name()
    {
        var source = Source()
            .AddTable("bars", Bars, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>("symbols", s => s.Ticker, verify: true))
            .Build();

        Assert.Single(Describe(source, "bars").ForeignKeys);
    }

    [Fact]
    public void A_row_type_no_table_is_registered_over_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(() => new PocoSourceBuilder("mem")
            .AddTable("bars", Bars, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>(s => s.Ticker, verify: false))
            .Build());

        Assert.Contains("Symbol", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_row_type_names_both_candidates()
    {
        var error = Assert.Throws<CatalogValidationException>(() => Source()
            .AddTable("symbols_copy", Symbols, t => t.UniqueKey(s => s.Ticker))
            .AddTable("bars", Bars, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>(s => s.Ticker, verify: false))
            .Build());

        Assert.Contains("'symbols'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'symbols_copy'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>F16(b): <c>verify: true</c> walks the child's rows and names the first orphan.</summary>
    [Fact]
    public void Verify_true_catches_an_orphan_and_names_it()
    {
        Bar[] orphaned = [new("BTCUSDT", 1), new("SOLUSDT", 2)];
        var error = Assert.Throws<CatalogVerificationException>(() => Source()
            .AddTable("bars", orphaned, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>(s => s.Ticker, verify: true))
            .Build());

        Assert.Equal("bars", error.Table);
        Assert.Equal(1, error.RowIndex);
        Assert.Contains("SOLUSDT", error.Message, StringComparison.Ordinal);
        Assert.Contains("symbols", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The same data with <c>verify: false</c> is Chalk taking the host's word for it.</summary>
    [Fact]
    public void Verify_false_performs_no_integrity_check()
    {
        Bar[] orphaned = [new("BTCUSDT", 1), new("SOLUSDT", 2)];
        var source = Source()
            .AddTable("bars", orphaned, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>(s => s.Ticker, verify: false))
            .Build();

        Assert.Single(Describe(source, "bars").ForeignKeys);
    }

    /// <summary>A NULL child key references nothing, so it is not an orphan (SQL's rule).</summary>
    [Fact]
    public void A_null_child_key_is_not_an_orphan()
    {
        Nullable[] rows = [new(null, 1), new("BTCUSDT", 2)];
        var source = Source()
            .AddTable("nullable_bars", rows, t => t
                .ForeignKey(b => b.Ticker).References<Symbol>(s => s.Ticker, verify: true))
            .Build();

        Assert.Single(Describe(source, "nullable_bars").ForeignKeys);
    }

    private sealed record Nullable(string? Ticker, long Volume);
}
