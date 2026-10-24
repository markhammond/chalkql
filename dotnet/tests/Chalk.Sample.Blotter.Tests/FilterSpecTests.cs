using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Postgres;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A filter on any column: the SQL it writes, the values it binds, and what it says it filters.</summary>
public sealed class FilterSpecTests
{
    private static readonly Tab Orders = Tabs.Orders;

    [Fact]
    public void Each_kind_of_column_writes_its_own_condition_over_bound_parameters()
    {
        var filter = FilterSpec.Of(
        [
            new ColumnFilter { Column = Orders.Named("client"), Values = ["FERN", "CEDAR", "FERN"] },
            new ColumnFilter { Column = Orders.Named("cl_ord_id"), Values = ["KELP-", "IVY_"], Match = TextMatch.StartsWith },
            new ColumnFilter { Column = Orders.Named("version"), Min = 2 },
            new ColumnFilter { Column = Orders.Named("qty"), Max = 10.5 },
            new ColumnFilter { Column = Orders.Named("order_time"), From = new DateTime(2026, 10, 23, 9, 0, 0, 1) },
        ]);

        Assert.Equal(
            "cl_ord_id LIKE @cl_ord_id_p0 ESCAPE '\\' OR cl_ord_id LIKE @cl_ord_id_p1 ESCAPE '\\'",
            filter.Where().Split(" AND ")[0].Trim('(', ')'));
        Assert.Contains("client IN @client_in", filter.Where(), StringComparison.Ordinal);
        Assert.Contains("base_version >= @version_min", filter.Where("base_"), StringComparison.Ordinal);

        var values = filter.Parameters();
        Assert.Equal(["CEDAR", "FERN"], (string[])values["client_in"]!);
        Assert.Equal("IVY\\_%", values["cl_ord_id_p0"]);
        Assert.Equal("KELP-%", values["cl_ord_id_p1"]);
        Assert.IsType<int>(values["version_min"]);
        Assert.IsType<double>(values["qty_max"]);
        Assert.Equal(new DateTime(2026, 10, 23, 9, 0, 0, 1), values["order_time_from"]);
    }

    [Fact]
    public void Filters_alike_have_one_name_whatever_order_they_were_given_in()
    {
        var one = FilterSpec.Of([
            new ColumnFilter { Column = Orders.Named("side"), Values = ["Sell", "Buy"] },
            new ColumnFilter { Column = Orders.Named("client"), Values = ["FERN"] },
        ]);
        var other = FilterSpec.Of([
            new ColumnFilter { Column = Orders.Named("client"), Values = ["FERN"] },
            new ColumnFilter { Column = Orders.Named("side"), Values = ["Buy", "Sell"] },
        ]);

        Assert.Equal(one.Name, other.Name);
    }

    [Fact]
    public void An_empty_column_filter_is_no_filter_and_without_takes_one_out()
    {
        var filter = FilterSpec.None.With(new ColumnFilter { Column = Orders.Named("client"), Values = ["FERN"] });

        Assert.Equal("TRUE", filter.With(new ColumnFilter { Column = Orders.Named("client") }).Where());
        Assert.Equal(FilterSpec.None.Name, filter.Without(Orders.Named("client")).Name);
    }

    [Fact]
    public void The_oracles_literal_says_the_same_with_values_written_in()
    {
        var filter = FilterSpec.Of([
            new ColumnFilter { Column = Orders.Named("client"), Values = ["O'NEIL"] },
            new ColumnFilter { Column = Orders.Named("cl_ord_id"), Values = ["KELP-"], Match = TextMatch.StartsWith },
            new ColumnFilter { Column = Orders.Named("order_id"), Values = ["0199a3b0-0000-7000-8000-000000000001", "nope"] },
            new ColumnFilter { Column = Orders.Named("version"), Min = 7 },
        ]);

        Assert.Equal(
            "(starts_with(cl_ord_id, 'KELP-')) AND client IN ('O''NEIL') AND order_id IN "
            + "('0199a3b0-0000-7000-8000-000000000001', '00000000-0000-0000-0000-000000000000') AND version >= 7",
            PostgresSql.Where(filter));
        Assert.Equal(
            [Guid.Parse("0199a3b0-0000-7000-8000-000000000001"), Guid.Empty],
            (Guid[])filter.Parameters()["order_id_in"]!);
    }

    [Fact]
    public void The_host_passes_a_row_exactly_as_the_database_would()
    {
        var order = SortSpecTests.Make(5, "BTC/USD Perp", 64_000) with { Version = 3, FilledQty = 2.5 };
        bool Passes(params ColumnFilter[] filters) => FilterSpec.Of(filters).Matches(order);

        Assert.True(Passes());
        Assert.True(Passes(new ColumnFilter { Column = Orders.Named("client"), Values = ["CEDAR", "FERN"] }));
        Assert.False(Passes(new ColumnFilter { Column = Orders.Named("client"), Values = ["cedar"] }));
        Assert.True(Passes(new ColumnFilter { Column = Orders.Named("instrument"), Values = ["BTC/"], Match = TextMatch.StartsWith }));
        Assert.False(Passes(new ColumnFilter { Column = Orders.Named("instrument"), Values = ["btc"], Match = TextMatch.StartsWith }));
        Assert.True(Passes(new ColumnFilter { Column = Orders.Named("order_id"), Values = [SortSpecTests.Id(5).ToString(), "nope"] }));
        Assert.False(Passes(new ColumnFilter { Column = Orders.Named("order_id"), Values = ["nope"] }));

        // A whole-number column binds its bound as a whole number, as the parameter would: 3.9 is 3.
        Assert.True(Passes(new ColumnFilter { Column = Orders.Named("version"), Min = 3.9 }));
        Assert.False(Passes(new ColumnFilter { Column = Orders.Named("filled_qty"), Max = 2.4 }));
        Assert.True(Passes(new ColumnFilter { Column = Orders.Named("order_time"), From = order.OrderTime }));
        Assert.False(Passes(new ColumnFilter { Column = Orders.Named("order_time"), To = order.OrderTime }));
    }

    [Fact]
    public void A_pill_says_what_its_column_is_filtered_by()
    {
        var pill = new ColumnFilter { Column = Orders.Named("client"), Values = ["A", "B", "C", "D", "E"] }.Describe();

        Assert.Equal("Client: A, B, C +2", pill);
    }

    [Fact]
    public void The_plans_estimates_say_how_many_rows_are_read_and_sorted_and_where()
    {
        const string pushed = """
            -- physical
            SourceToLocalConverter: rowcount = 1687.7, cumulative cost = {}, id = 266
              SourceSort(sort0=[$4], offset=[?4], fetch=[?3]): rowcount = 1687.7, cumulative cost = {}, id = 265
                SourceFilter(condition=[=($0, ?0)]): rowcount = 75009.3, cumulative cost = {}, id = 263
                  SourceScan(table=[[blotter, orders_current]]): rowcount = 500062.0, cumulative cost = {}, id = 215
            """;
        const string local = """
            -- physical
            ChalkTopN(collation=[[3 DESC]], offset=[?2], fetch=[?1]): rowcount = 75009.3, cumulative cost = {}, id = 152
              ChalkFilter(condition=[=($0, ?0)]): rowcount = 75009.3, cumulative cost = {}, id = 150
                SourceToLocalConverter: rowcount = 500062.0, cumulative cost = {}, id = 149
                  SourceScan(table=[[blotter, orders_current]]): rowcount = 500062.0, cumulative cost = {}, id = 99
            """;

        var inDatabase = SourceQueries.Estimate(pushed);
        var inHost = SourceQueries.Estimate(local);

        Assert.Equal((500062.0, 75009.3, true, true), (inDatabase.RowsRead, inDatabase.RowsSorted, inDatabase.SortInDatabase, inDatabase.FilterInDatabase));
        Assert.Equal((500062.0, 75009.3, false, false), (inHost.RowsRead, inHost.RowsSorted, inHost.SortInDatabase, inHost.FilterInDatabase));
    }
}
