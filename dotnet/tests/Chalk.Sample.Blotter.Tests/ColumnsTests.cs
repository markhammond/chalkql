using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>How the grid writes values, and the columns it only shows.</summary>
public sealed class ColumnsTests
{
    [Theory]
    [InlineData(0.25, ".25")]
    [InlineData(-0.05, "-.05")]
    [InlineData(60000, "60,000")]
    [InlineData(2.5, "2.5")]
    [InlineData(0, "–")]
    public void A_quantity_drops_the_zeros_that_do_not_count_and_nought_is_a_dash(double value, string shown) =>
        Assert.Equal(shown, Column.Finance(value, 4));

    [Theory]
    [InlineData(64258.6045, "64,258.6")]
    [InlineData(0.1198, ".1198")]
    [InlineData(3219.61234, "3,219.61")]
    [InlineData(1234567, "1,234,570")]
    [InlineData(0.00123456789, ".00123457")]
    [InlineData(123456789, "1.23457e8")]
    [InlineData(0.0000123456789, "1.23457e-5")]
    [InlineData(0, "–")]
    public void A_price_is_six_significant_figures_and_scientific_past_eight_digits(double value, string shown) =>
        Assert.Equal(shown, Column.Significant(value));

    [Fact]
    public void A_column_only_shown_is_refused_as_a_sort_or_a_filter()
    {
        var filled = Tab.Orders.Named("filled_pct");
        Assert.True(filled.Derived);

        var sort = Assert.Throws<ArgumentException>(() => SortSpec.Of(Tab.Orders, new SortColumn(filled, Descending: true)));
        Assert.Contains("nothing is sorted by it", sort.Message, StringComparison.Ordinal);
        var filter = Assert.Throws<ArgumentException>(() => FilterSpec.Of([new ColumnFilter { Column = filled, Values = ["50%"] }]));
        Assert.Contains("nothing is filtered by it", filter.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grid_shows_times_with_their_date_to_the_second_and_an_order_page_to_the_microsecond()
    {
        var time = Tab.Fills.Named("event_time");
        var fill = new Fill(
            Guid.CreateVersion7(), Guid.CreateVersion7(), new DateTime(2026, 10, 24, 3, 4, 5, 678, 901, DateTimeKind.Utc),
            "ACORN-1", "ACORN", "EMEA", "API", "BTC/USD Perp", "Buy", 1, 2, 2, 0);

        Assert.Equal("2026-10-24 03:04:05", time.Show(fill));
        Assert.Equal("2026-10-24 03:04:05.678901", time.Detail(fill));
        Assert.DoesNotContain(Tab.All.SelectMany(t => t.Columns), c => c.Name == "date");
    }
}
