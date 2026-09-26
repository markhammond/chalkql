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
