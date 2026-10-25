using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A view as a link: written, read back the same, and refused whole where it is not understood.</summary>
public sealed class ViewLinkTests
{
    private static readonly Tab Orders = Tabs.Orders;

    [Fact]
    public void The_tab_as_it_opens_is_an_empty_link_and_reads_back_so()
    {
        var link = new ViewLink(Orders, Orders.DefaultSort, FilterSpec.None, Orders.HiddenAtFirst, null);
        Assert.Equal("", link.Query());

        var back = ViewLink.Parse("", Tabs.All);
        Assert.Equal(Orders, back.Tab);
        Assert.True(back.Sort.IsDefault);
        Assert.True(back.Filter.Columns.IsEmpty);
        Assert.True(back.Hidden.SetEquals(Orders.HiddenAtFirst));
        Assert.Null(back.At);
    }

    [Fact]
    public void A_sort_filters_hidden_columns_and_the_row_held_at_read_back_the_same()
    {
        var sort = SortSpec.Of(Orders, new SortColumn(Orders.Named("client"), Descending: true), new SortColumn(Orders.Named("qty"), Descending: false));
        var filter = FilterSpec.Of(
        [
            new ColumnFilter { Column = Orders.Named("client"), Values = ["CEDAR", "FERN & CO"] },
            new ColumnFilter { Column = Orders.Named("instrument"), Values = ["BTC"], Match = TextMatch.StartsWith },
            new ColumnFilter { Column = Orders.Named("side"), Values = ["Buy"] },
            new ColumnFilter { Column = Orders.Named("qty"), Min = 0.25, Max = 1e6 },
            new ColumnFilter { Column = Orders.Named("order_time"), From = Cells.Micros(new DateTime(2026, 10, 24, 1, 2, 3).AddTicks(45670)) },
        ]);
        var at = new Order(
            Guid.CreateVersion7(), 3, default, default, "CEDAR-1", "CEDAR", "", "", "", "", 0.15, 0, 0, 0, 0, "");
        var link = new ViewLink(Orders, sort, filter, new HashSet<string> { "region" }, at);

        var back = ViewLink.Parse("?" + link.Query(), Tabs.All);

        Assert.Equal(sort, back.Sort);
        Assert.Equal(filter.Name, back.Filter.Name);
        Assert.True(back.Hidden.SetEquals(["region"]));
        Assert.NotNull(back.At);
        Assert.All(sort.Keys, k => Assert.Equal(k.Column.Value(at), k.Column.Value(back.At)));
        Assert.Contains("sort=-client%2Cqty", link.Query(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fills_held_at_a_row_in_their_own_order_read_back_the_same()
    {
        var fills = Tabs.Fills;
        var at = new Fill(Guid.CreateVersion7(), Guid.CreateVersion7(), Cells.Micros(new DateTime(2026, 10, 24, 3, 4, 5).AddTicks(123450)),
            "", "", "", "", "", "", 0, 0, 0, 0, 0);
        var link = new ViewLink(fills, fills.DefaultSort, FilterSpec.None, new HashSet<string>(), at);

        var back = ViewLink.Parse(link.Query(), Tabs.All);

        Assert.Equal(fills, back.Tab);
        Assert.True(back.Sort.IsDefault);
        Assert.Empty(back.Hidden);
        Assert.IsType<KeyRow>(back.At);
        Assert.Equal(at.EventTime, fills.Time.Value(back.At));
        Assert.Equal(at.FillId, back.At.Id);
    }

    [Fact]
    public void Shrouded_the_link_shows_no_filter_value_or_held_row_and_reads_back_the_same()
    {
        var shroud = new LinkShroud(new EphemeralDataProtectionProvider());
        var (link, at) = Held();

        var query = link.Query(shroud);
        var back = ViewLink.Parse(query, Tabs.All, shroud);

        Assert.DoesNotContain("CEDAR", query, StringComparison.Ordinal);
        Assert.DoesNotContain("f.", query, StringComparison.Ordinal);
        Assert.DoesNotContain("at.", query, StringComparison.Ordinal);
        Assert.DoesNotContain(at.OrderId.ToString(), query, StringComparison.Ordinal);
        Assert.StartsWith("sort=-client&hide=region&s=", query, StringComparison.Ordinal);
        Assert.Equal(link.Sort, back.Sort);
        Assert.Equal(link.Filter.Name, back.Filter.Name);
        Assert.Equal(at.OrderId, back.At!.Id);
    }

    [Fact]
    public void A_link_may_give_some_parts_as_they_are_and_some_shrouded_but_none_both_ways()
    {
        var shroud = new LinkShroud(new EphemeralDataProtectionProvider());
        var (link, _) = Held();
        var shrouded = link.Query(shroud);

        var mixed = ViewLink.Parse(shrouded + "&f.side=Buy", Tabs.All, shroud);
        Assert.Contains(mixed.Filter.Columns, f => f.Column.Name == "side");
        Assert.Contains(mixed.Filter.Columns, f => f.Column.Name == "client");

        var both = Assert.ThrowsAny<ArgumentException>(() => ViewLink.Parse(shrouded + "&f.client=FERN", Tabs.All, shroud));
        Assert.Contains("'f.client' is given both as is and shrouded", both.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shrouded_part_altered_or_shrouded_by_other_keys_or_without_a_shroud_is_refused()
    {
        var shroud = new LinkShroud(new EphemeralDataProtectionProvider());
        var (link, _) = Held();
        var query = link.Query(shroud);
        var at = query.IndexOf("s=", StringComparison.Ordinal) + 10;
        var altered = query[..at] + (query[at] == 'A' ? 'B' : 'A') + query[(at + 1)..];

        Assert.Contains("altered", Assert.ThrowsAny<ArgumentException>(() => ViewLink.Parse(altered, Tabs.All, shroud)).Message, StringComparison.Ordinal);
        Assert.Contains("another blotter's keys", Assert.ThrowsAny<ArgumentException>(
            () => ViewLink.Parse(query, Tabs.All, new LinkShroud(new EphemeralDataProtectionProvider()))).Message, StringComparison.Ordinal);
        Assert.Contains("cannot be opened here", Assert.ThrowsAny<ArgumentException>(() => ViewLink.Parse(query, Tabs.All)).Message, StringComparison.Ordinal);
        Assert.Contains("which alone are shrouded", Assert.ThrowsAny<ArgumentException>(
            () => ViewLink.Parse("s=" + shroud.Shroud("sort=qty"), Tabs.All, shroud)).Message, StringComparison.Ordinal);
    }

    private static (ViewLink Link, Order At) Held()
    {
        var sort = SortSpec.Of(Orders, new SortColumn(Orders.Named("client"), Descending: true));
        var filter = FilterSpec.Of([new ColumnFilter { Column = Orders.Named("client"), Values = ["CEDAR"] }]);
        var at = new Order(Guid.CreateVersion7(), 1, default, default, "", "CEDAR", "", "", "", "", 0, 0, 0, 0, 0, "");
        return (new ViewLink(Orders, sort, filter, new HashSet<string> { "region" }, at), at);
    }

    [Theory]
    [InlineData("tab=positions", "there is no tab 'positions'")]
    [InlineData("sort=-colour", "Client orders has no column 'colour'")]
    [InlineData("sort=filled_pct", "nothing is sorted by it")]
    [InlineData("f.filled_pct=50%25", "nothing is filtered by it")]
    [InlineData("hide=order_id", "Order is always shown")]
    [InlineData("f.side=Hold", "Side is never 'Hold'")]
    [InlineData("f.qty=12", "Qty has no filter 'f.qty'")]
    [InlineData("f.qty.min=lots", "'lots' is not a value of Qty")]
    [InlineData("f.qty.min=1&f.qty.min=2", "'f.qty.min' is given 2 times")]
    [InlineData("f.client=A&f.client.starts=B", "filtered by values and by what they start with at once")]
    [InlineData("at.order_time=2026-10-24T01:02:03.000000", "where this sort needs at.order_time, at.order_id")]
    [InlineData("page=2", "'page' is not part of a view")]
    public void A_link_not_understood_is_refused_saying_what(string query, string why)
    {
        var refused = Assert.ThrowsAny<ArgumentException>(() => ViewLink.Parse(query, Tabs.All));
        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }
}
