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

    /// <summary>
    /// A shape taken from the last one, reading only what moved (F205), is the shape taken whole: a
    /// row count moved over the same parts, statistics moved in new columns, and a join policy moved.
    /// </summary>
    [Fact]
    public void A_shape_taken_by_part_is_the_shape_taken_whole()
    {
        var bars = Model("bars", 1_000, Statistics(7));
        var quotes = Model("quotes", 50, Statistics(3));
        var steps = new[]
        {
            Catalog(bars, quotes),
            Catalog(bars, Model("quotes", 55, quotes.Columns)),
            Catalog(bars, Model("quotes", 70, Statistics(9))),
        };
        steps = [.. steps, Catalog(bars, steps[2].Schemas[0].Tables[1], new Chalk.Catalog.CrossSourceJoinPolicy { LocalJoinMaxRows = 5 })];

        var shape = CatalogShape.Of(steps[0]);
        for (var i = 1; i < steps.Length; i++)
        {
            var byPart = shape.Next(steps[i - 1], steps[i]);
            Assert.NotNull(byPart);
            AssertSameShape(CatalogShape.Of(steps[i]), byPart);
            shape = byPart;
        }
    }

    /// <summary>A table read alone whose shape moved is not taken by part: the whole shape is.</summary>
    [Fact]
    public void A_table_read_alone_whose_shape_moved_is_taken_whole()
    {
        var bars = Model("bars", 1_000, Statistics(7));
        var before = Catalog(bars);
        Chalk.Catalog.ColumnDescriptor[] retyped =
        [
            new() { Name = "symbol", Type = Chalk.Catalog.ChalkType.String(), Statistics = bars.Columns[0].Statistics },
            new() { Name = "close", Type = Chalk.Catalog.ChalkType.Float32(), Statistics = bars.Columns[1].Statistics },
        ];
        var after = Catalog(Model("bars", 1_000, retyped, bars));

        Assert.Null(CatalogShape.Of(before).Next(before, after));
        Assert.Equal(
            [new TableKey("mem", "main", "bars")],
            CatalogShape.Of(after).ShapeChangesSince(CatalogShape.Of(before)));
    }

    /// <summary>A shape steps only from the catalog it was taken of.</summary>
    [Fact]
    public void A_shape_steps_only_from_the_catalog_it_was_taken_of()
    {
        var bars = Model("bars", 1_000, Statistics(7));
        var taken = Catalog(bars);
        var other = Catalog(bars);
        var next = Catalog(Model("bars", 1_001, bars.Columns, bars));

        Assert.Null(CatalogShape.Of(taken).Next(other, next));
        Assert.Null(CatalogShape.Of(Wire()).Next(taken, next));
    }

    private static void AssertSameShape(CatalogShape whole, CatalogShape byPart)
    {
        Assert.Equal(whole.Tables.Count, byPart.Tables.Count);
        Assert.Equal(whole.Statistics.Count, byPart.Statistics.Count);
        foreach (var (key, shape) in whole.Tables)
        {
            Assert.Equal(shape, byPart.Tables[key]);
            Assert.Equal(whole.Statistics[key].RowCount, byPart.Statistics[key].RowCount);
            Assert.Equal(whole.Statistics[key].Rest, byPart.Statistics[key].Rest);
        }

        Assert.Equal(whole.Policies, byPart.Policies);
    }

    private static readonly IReadOnlyList<Chalk.Catalog.IndexDescriptor> Indexes =
        [new Chalk.Catalog.IndexDescriptor { Name = "ix_symbol", Kind = Chalk.Ir.IndexKind.Hash, Columns = [0] }];

    private static Chalk.Catalog.ColumnDescriptor[] Statistics(long distinct) =>
    [
        new()
        {
            Name = "symbol",
            Type = Chalk.Catalog.ChalkType.String(),
            Statistics = new Chalk.Catalog.ColumnStatistics
            {
                Level = Chalk.Ir.StatisticsLevel.Basic, DistinctCount = distinct, NullCount = 0, Min = "A", Max = "Z",
            },
        },
        new()
        {
            Name = "close",
            Type = Chalk.Catalog.ChalkType.Float64(),
            Statistics = new Chalk.Catalog.ColumnStatistics
            {
                Level = Chalk.Ir.StatisticsLevel.Basic, DistinctCount = distinct * 10, NullCount = 0, Min = 1.5, Max = 99.5,
            },
        },
    ];

    /// <summary>A table over <paramref name="columns"/>, sharing every other part with <paramref name="like"/> when given.</summary>
    private static Chalk.Catalog.TableDescriptor Model(
        string name,
        long rows,
        IReadOnlyList<Chalk.Catalog.ColumnDescriptor> columns,
        Chalk.Catalog.TableDescriptor? like = null) => new()
    {
        Name = name,
        RowCount = rows,
        RowCountKind = Chalk.Ir.RowCountKind.Exact,
        Columns = columns,
        Indexes = like?.Indexes ?? Indexes,
        UniqueKeys = like?.UniqueKeys ?? [],
        Collations = like?.Collations ?? [],
        ForeignKeys = like?.ForeignKeys ?? [],
    };

    private static readonly IReadOnlyList<Chalk.Catalog.FunctionDescriptor> NoFunctions = [];

    private static readonly IReadOnlyList<Chalk.Catalog.AssociationDescriptor> NoAssociations = [];

    private static Chalk.Catalog.CatalogContext Catalog(
        Chalk.Catalog.TableDescriptor first,
        Chalk.Catalog.TableDescriptor? second = null,
        Chalk.Catalog.CrossSourceJoinPolicy? policy = null) => new()
    {
        ContextId = "shape",
        Epoch = 1,
        Schemas =
        [
            new Chalk.Catalog.SchemaDescriptor
            {
                SourceId = "mem",
                Name = "main",
                Kind = Chalk.Ir.SourceKind.Local,
                Functions = NoFunctions,
                Tables = second is null ? [first] : [first, second],
            },
        ],
        JoinPolicy = policy ?? Chalk.Catalog.CrossSourceJoinPolicy.Default,
        Associations = NoAssociations,
    };

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
