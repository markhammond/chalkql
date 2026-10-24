using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A view as a link: written, read back the same, and refused whole where it is not understood.</summary>
public sealed class ViewLinkTests
{
    private static readonly Tab Orders = Tab.Orders;

    [Fact]
    public void The_tab_as_it_opens_is_an_empty_link_and_reads_back_so()
    {
        var link = new ViewLink(Orders, Orders.DefaultSort, FilterSpec.None, Orders.HiddenAtFirst, null);
        Assert.Equal("", link.Query());

        var back = ViewLink.Parse("");
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
            Guid.CreateVersion7(), 3, default, default, "CEDAR-1", "CEDAR", "", "", "", "", 0.15, 0, 0, 0, "");
        var link = new ViewLink(Orders, sort, filter, new HashSet<string> { "region" }, at);

        var back = ViewLink.Parse("?" + link.Query());

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
        var fills = Tab.Fills;
        var at = new Fill(Guid.CreateVersion7(), Guid.CreateVersion7(), Cells.Micros(new DateTime(2026, 10, 24, 3, 4, 5).AddTicks(123450)),
            "", "", "", "", "", "", 0, 0, 0, 0);
        var link = new ViewLink(fills, fills.DefaultSort, FilterSpec.None, new HashSet<string>(), at);

        var back = ViewLink.Parse(link.Query());

        Assert.Equal(fills, back.Tab);
        Assert.True(back.Sort.IsDefault);
        Assert.Empty(back.Hidden);
        Assert.Equal(at.EventTime, back.At!.EventTime);
        Assert.Equal(at.FillId, back.At.Id);
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
        var refused = Assert.ThrowsAny<ArgumentException>(() => ViewLink.Parse(query));
        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }
}
