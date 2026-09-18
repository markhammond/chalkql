using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// §5.4 / D17. A lying collation or unique key is a silent wrong-answer bug, so <c>Build()</c> checks
/// both by default and names the first offending row.
/// </summary>
public sealed class PocoVerificationTests
{
    [Fact]
    public void An_unsorted_collection_is_rejected_with_the_offending_row_index()
    {
        var rows = new List<Point> { new(1, "a"), new(5, "b"), new(3, "c"), new(9, "d") };

        var error = Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.OrderedBy(r => r.Id))
            .Build());

        Assert.Equal("points", error.Table);
        Assert.Equal(2, error.RowIndex);
        Assert.Contains("collation [Id ASC NULLS LAST]", error.Message, StringComparison.Ordinal);
        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secondary_key_is_only_checked_where_the_primary_one_ties()
    {
        var rows = new List<Point> { new(1, "b"), new(1, "a"), new(2, "z") };

        var error = Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.OrderedBy(r => r.Id).ThenBy(r => r.Name))
            .Build());

        Assert.Equal(1, error.RowIndex);
        Assert.Contains("Name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_descending_collation_is_verified_in_its_own_direction()
    {
        var descending = new List<Point> { new(9, "a"), new(5, "b"), new(1, "c") };

        new PocoSourceBuilder("mem")
            .AddTable("points", descending, t => t.OrderedBy(r => r.Id, SortDirection.DescNullsLast))
            .Build();

        Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", descending, t => t.OrderedBy(r => r.Id))
            .Build());
    }

    [Fact]
    public void Nulls_are_verified_where_the_declared_direction_puts_them()
    {
        var nullsLast = new List<Point> { new(1, "a"), new(2, "b"), new(null, "c") };

        new PocoSourceBuilder("mem")
            .AddTable("points", nullsLast, t => t.OrderedBy(r => r.Id))
            .Build();

        var error = Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", nullsLast, t => t.OrderedBy(r => r.Id, SortDirection.AscNullsFirst))
            .Build());

        Assert.Equal(2, error.RowIndex);
    }

    [Fact]
    public void A_duplicate_unique_key_is_rejected_with_the_offending_row_index()
    {
        var rows = new List<Point> { new(1, "a"), new(2, "b"), new(1, "c") };

        var error = Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.UniqueKey(r => r.Id))
            .Build());

        Assert.Equal("points", error.Table);
        Assert.Equal(2, error.RowIndex);
        Assert.Contains("unique key (Id)", error.Message, StringComparison.Ordinal);
        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_unique_key_only_fails_when_the_whole_tuple_repeats()
    {
        var rows = new List<Point> { new(1, "a"), new(1, "b"), new(2, "a") };

        new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.UniqueKey(r => r.Id, r => r.Name))
            .Build();

        rows.Add(new Point(1, "a"));

        var error = Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.UniqueKey(r => r.Id, r => r.Name))
            .Build());

        Assert.Equal(3, error.RowIndex);
    }

    [Fact]
    public void Verify_false_accepts_an_unsorted_collection_and_a_duplicate_key()
    {
        var rows = new List<Point> { new(5, "a"), new(1, "a"), new(5, "a") };

        var source = new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t
                .OrderedBy(r => r.Id)
                .UniqueKey(r => r.Id, r => r.Name)
                .Verify(false))
            .Build();

        var table = source.DescribeSchema().Tables[0];
        Assert.Single(table.Collations);
        Assert.Single(table.UniqueKeys);
    }

    /// <summary>
    /// An enum column is STRING, so its declared order is the order of the names — which is not the
    /// order of the underlying numbers. Verification has to check what the planner will see.
    /// </summary>
    [Fact]
    public void An_enum_collation_is_verified_in_string_order_not_clr_order()
    {
        var byName = new List<Shaded> { new(Colour.Blue), new(Colour.Green), new(Colour.Red) };

        new PocoSourceBuilder("mem")
            .AddTable("shades", byName, t => t.OrderedBy(r => r.Shade))
            .Build();

        var byNumber = new List<Shaded> { new(Colour.Red), new(Colour.Green), new(Colour.Blue) };

        Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("shades", byNumber, t => t.OrderedBy(r => r.Shade))
            .Build());
    }

    [Fact]
    public void A_string_collation_is_verified_in_ordinal_order()
    {
        // Ordinal puts every uppercase letter before every lowercase one; a culture-aware comparison
        // would interleave them and accept a list the executor would treat as unsorted.
        var rows = new List<Point> { new(1, "Z"), new(2, "a") };

        new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.OrderedBy(r => r.Name))
            .Build();

        Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", [new Point(1, "a"), new Point(2, "Z")], t => t.OrderedBy(r => r.Name))
            .Build());
    }

    [Fact]
    public void Verification_reads_the_collection_the_func_returns()
    {
        var rows = new List<Point> { new(1, "a"), new(2, "b") };

        new PocoSourceBuilder("mem")
            .AddTable("points", () => rows, t => t.OrderedBy(r => r.Id))
            .Build();

        rows.Reverse();

        Assert.Throws<CatalogVerificationException>(() => new PocoSourceBuilder("mem")
            .AddTable("points", () => rows, t => t.OrderedBy(r => r.Id))
            .Build());
    }

    public sealed record Point(int? Id, string Name);

    public sealed record Shaded(Colour Shade);
}
