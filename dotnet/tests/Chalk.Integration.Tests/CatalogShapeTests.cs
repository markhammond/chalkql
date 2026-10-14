using Chalk.Client;

namespace Chalk.Integration.Tests;

/// <summary>
/// A table's shape carries its schema's structural properties (F145): a change to what a source can
/// take, or to what it is trusted to enforce, moves every table of the schema, while the numbers, the
/// join policy and the cost profile move nothing.
/// </summary>
public sealed class CatalogShapeTests
{
    private static readonly TableKey Orders = new("pg", "warehouse", "orders");

    [Fact]
    public void A_capabilities_change_moves_every_table_of_the_schema()
    {
        var before = CatalogShape.Of(Wire());
        var after = CatalogShape.Of(Wire(s => s.Capabilities.MaxInList = 10));

        Assert.Equal([Orders], after.ShapeChangesSince(before));
        Assert.Empty(after.StatisticsChangesSince(before));
    }

    [Fact]
    public void A_predicate_shape_withdrawn_moves_the_shape()
    {
        var before = CatalogShape.Of(Wire());
        var after = CatalogShape.Of(Wire(s => s.Capabilities.PushablePredicates.Remove(Chalk.Ir.PredicateShape.In)));

        Assert.Equal([Orders], after.ShapeChangesSince(before));
    }

    [Fact]
    public void The_row_level_security_trust_the_dialect_and_the_zone_are_shape()
    {
        var before = CatalogShape.Of(Wire());

        Assert.Equal([Orders], CatalogShape.Of(Wire(s => s.TrustSourceRowLevelSecurity = true)).ShapeChangesSince(before));
        Assert.Equal([Orders], CatalogShape.Of(Wire(s => s.Dialect = "postgres")).ShapeChangesSince(before));
        Assert.Equal([Orders], CatalogShape.Of(Wire(s => s.Zone = "eu")).ShapeChangesSince(before));
    }

    [Fact]
    public void The_numbers_the_join_policy_and_the_cost_profile_move_no_shape()
    {
        var before = CatalogShape.Of(Wire());

        var moved = CatalogShape.Of(Wire(s => s.Tables[0].RowCount = 99));
        Assert.Empty(moved.ShapeChangesSince(before));
        Assert.Equal([Orders], moved.StatisticsChangesSince(before));

        var policy = Wire();
        policy.JoinPolicy = new Chalk.Ir.CrossSourceJoinPolicy { LocalJoinMaxRows = 5 };
        Assert.Empty(CatalogShape.Of(policy).ShapeChangesSince(before));

        Assert.Empty(CatalogShape.Of(Wire(s => s.CostProfile = new Chalk.Ir.CostProfile { ScanRowCost = 2 })).ShapeChangesSince(before));
    }

    /// <summary>
    /// A row count moves the numbers once it has drifted a tenth from the count held, either way,
    /// and not before (D351): the planner is not sent every append's count.
    /// </summary>
    [Fact]
    public void A_row_count_moves_the_numbers_once_it_has_drifted_a_tenth()
    {
        var held = Numbers(1_000).Statistics;

        Assert.Empty(Numbers(1_099).StatisticsChangesSince(held));
        Assert.Equal([Orders], Numbers(1_100).StatisticsChangesSince(held));
        Assert.Empty(Numbers(901).StatisticsChangesSince(held));
        Assert.Equal([Orders], Numbers(900).StatisticsChangesSince(held));
    }

    /// <summary>
    /// The drift is counted from the count the planner holds, so small moves add up to a send, and a
    /// send moves where it is counted from.
    /// </summary>
    [Fact]
    public void Drift_is_counted_from_the_count_held()
    {
        var held = Numbers(1_000).Statistics;
        Assert.Empty(Numbers(1_050).StatisticsChangesSince(held));
        Assert.Empty(Numbers(1_099).StatisticsChangesSince(held));

        var moved = Numbers(1_100);
        Assert.Equal([Orders], moved.StatisticsChangesSince(held));
        held = moved.NumbersSent(held, [Orders]);

        Assert.Empty(Numbers(1_200).StatisticsChangesSince(held));
        Assert.Equal([Orders], Numbers(1_210).StatisticsChangesSince(held));
    }

    /// <summary>A count that is or was unknown, or was zero, has moved by any change.</summary>
    [Fact]
    public void An_unknown_or_empty_count_moves_by_any_change()
    {
        Assert.Equal([Orders], Numbers(5).StatisticsChangesSince(Numbers(-1).Statistics));
        Assert.Equal([Orders], Numbers(-1).StatisticsChangesSince(Numbers(5).Statistics));
        Assert.Equal([Orders], Numbers(1).StatisticsChangesSince(Numbers(0).Statistics));
        Assert.Empty(Numbers(-1).StatisticsChangesSince(Numbers(-1).Statistics));
    }

    /// <summary>Every number but the count moves the numbers however little it moved.</summary>
    [Fact]
    public void Every_other_number_moves_however_little()
    {
        var held = Numbers(1_000).Statistics;

        Assert.Equal(
            [Orders],
            Numbers(1_000, t => t.RowCountKind = Chalk.Ir.RowCountKind.Estimate).StatisticsChangesSince(held));
        Assert.Equal(
            [Orders],
            Numbers(1_001, t => t.Columns[0].Statistics = new Chalk.Ir.ColumnStatistics { NullCount = 1 })
                .StatisticsChangesSince(held));
    }

    private static CatalogShape Numbers(long rows, Action<Chalk.Ir.Table>? change = null) =>
        CatalogShape.Of(Wire(s =>
        {
            s.Tables[0].RowCount = rows;
            change?.Invoke(s.Tables[0]);
        }));

    private static Chalk.Ir.CatalogContext Wire(Action<Chalk.Ir.Schema>? change = null)
    {
        var schema = new Chalk.Ir.Schema
        {
            SourceId = "pg",
            Name = "warehouse",
            Kind = Chalk.Ir.SourceKind.Remote,
            Dialect = "duckdb",
            Capabilities = new Chalk.Ir.SourceCapabilities
            {
                QueryLanguage = Chalk.Ir.QueryLanguage.Sql,
                MaxInList = 100,
                PushablePredicates = { Chalk.Ir.PredicateShape.In },
            },
            Tables =
            {
                new Chalk.Ir.Table
                {
                    Name = "orders",
                    RowCount = 10,
                    Columns = { new Chalk.Ir.Column { Name = "id", Type = new Chalk.Ir.Type { Kind = Chalk.Ir.TypeKind.I32 } } },
                },
            },
        };
        change?.Invoke(schema);
        return new Chalk.Ir.CatalogContext { ContextId = "shape", Epoch = 1, Schemas = { schema } };
    }
}
