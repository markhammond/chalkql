using System.Reflection;
using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// What moved between two catalogs, read by reference (F205): a table that is the descriptor it was
/// moved nothing, one over the same parts with a new row count moved its count only, one with new
/// columns moved its numbers, and any other new part is read as the catalog moving whole. A catalog
/// whose change moved only counts is validated for what it moved.
/// </summary>
public sealed class CatalogChangeTests
{
    private static readonly IReadOnlyList<ColumnDescriptor> Columns =
    [
        new() { Name = "symbol", Type = ChalkType.String() },
        new() { Name = "ts", Type = ChalkType.Timestamp(9) },
    ];

    private static readonly IReadOnlyList<IndexDescriptor> Indexes =
        [new IndexDescriptor { Name = "ix_symbol", Kind = IndexKind.Hash, Columns = [0] }];

    private static readonly IReadOnlyList<AssociationDescriptor> Associations = [];

    private static readonly IReadOnlyList<FunctionDescriptor> Functions = [];

    private static TableDescriptor Table(
        string name,
        long rows,
        IReadOnlyList<ColumnDescriptor>? columns = null,
        IReadOnlyList<IndexDescriptor>? indexes = null) => new()
    {
        Name = name,
        RowCount = rows,
        RowCountKind = RowCountKind.Exact,
        Columns = columns ?? Columns,
        Indexes = indexes ?? Indexes,
    };

    private static SchemaDescriptor Schema(SourceCapabilities? capabilities = null, params TableDescriptor[] tables) => new()
    {
        SourceId = "mem",
        Name = "main",
        Kind = SourceKind.Local,
        Capabilities = capabilities ?? SourceCapabilities.None,
        Functions = Functions,
        Tables = tables,
    };

    private static CatalogContext Catalog(
        SchemaDescriptor schema, CrossSourceJoinPolicy? policy = null, long epoch = 1) => new()
    {
        ContextId = "demo",
        Epoch = epoch,
        Schemas = [schema],
        JoinPolicy = policy ?? CrossSourceJoinPolicy.Default,
        Associations = Associations,
    };

    [Theory]
    [InlineData(typeof(CatalogContext))]
    [InlineData(typeof(SchemaDescriptor))]
    [InlineData(typeof(TableDescriptor))]
    public void Every_member_of_a_descriptor_is_read(System.Type descriptor)
    {
        var read = descriptor == typeof(CatalogContext) ? CatalogChange.CatalogMembers
            : descriptor == typeof(SchemaDescriptor) ? CatalogChange.SchemaMembers
            : CatalogChange.TableMembers;
        var members = descriptor.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

        Assert.Equal(members.Order(), read.Order());
    }

    [Fact]
    public void Tables_that_are_the_descriptors_they_were_moved_nothing()
    {
        var bars = Table("bars", 100);
        var quotes = Table("quotes", 50);

        var change = CatalogChange.Between(
            Catalog(Schema(null, bars, quotes)), Catalog(Schema(null, bars, quotes), epoch: 2));

        Assert.False(change.Structural);
        Assert.True(change.CountsOnly);
        Assert.False(change.JoinPolicyMoved);
        Assert.Empty(change.Tables);
    }

    [Fact]
    public void A_new_row_count_over_the_same_parts_moved_the_count_only()
    {
        var bars = Table("bars", 100);
        var quotes = Table("quotes", 50);

        var change = CatalogChange.Between(
            Catalog(Schema(null, bars, quotes)), Catalog(Schema(null, bars, Table("quotes", 55))));

        Assert.True(change.CountsOnly);
        var moved = Assert.Single(change.Tables);
        Assert.Equal((0, 1), (moved.Schema, moved.Table));
        Assert.True(moved.CountOnly);
    }

    [Fact]
    public void New_columns_moved_the_numbers_and_not_the_count_only()
    {
        var bars = Table("bars", 100);
        IReadOnlyList<ColumnDescriptor> restated = [.. Columns];

        var change = CatalogChange.Between(
            Catalog(Schema(null, bars)), Catalog(Schema(null, Table("bars", 100, columns: restated))));

        Assert.False(change.Structural);
        Assert.False(change.CountsOnly);
        Assert.False(Assert.Single(change.Tables).CountOnly);
    }

    [Fact]
    public void Any_other_new_part_moves_the_catalog_whole()
    {
        var bars = Table("bars", 100);
        var before = Catalog(Schema(null, bars));

        Assert.True(CatalogChange.Between(before, Catalog(Schema(null, Table("bars", 100, indexes: [.. Indexes])))).Structural);
        Assert.True(CatalogChange.Between(before, Catalog(Schema(new SourceCapabilities(), bars))).Structural);
        Assert.True(CatalogChange.Between(before, Catalog(Schema(null, bars, Table("quotes", 1)))).Structural);
        Assert.True(CatalogChange.Between(before, new CatalogContext
        {
            ContextId = "demo",
            Schemas = before.Schemas,
            Associations = new List<AssociationDescriptor>(),
        }).Structural);
    }

    [Fact]
    public void A_new_join_policy_moved_the_policy_only()
    {
        var schema = Schema(null, Table("bars", 100));

        var change = CatalogChange.Between(
            Catalog(schema), Catalog(schema, new CrossSourceJoinPolicy { LocalJoinMaxRows = 5 }));

        Assert.True(change.CountsOnly);
        Assert.True(change.JoinPolicyMoved);
    }

    /// <summary>
    /// A change that moved counts only is validated for them: a count below -1 is refused as the whole
    /// catalog's validation refuses it, naming the same table, and so is a join policy that moved.
    /// </summary>
    [Fact]
    public void A_change_is_validated_for_what_it_moved()
    {
        var bars = Table("bars", 100);
        var before = Catalog(Schema(null, bars));
        var after = Catalog(Schema(null, Table("bars", -5)));

        var byPart = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.ValidateChange(after, CatalogChange.Between(before, after)));
        var whole = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(after));
        Assert.Equal(whole.Path, byPart.Path);
        Assert.Equal(whole.Message, byPart.Message);

        var policy = Catalog(before.Schemas[0], new CrossSourceJoinPolicy { BroadcastMaxRows = -1 });
        Assert.Equal(
            "join_policy",
            Assert.Throws<CatalogValidationException>(
                () => CatalogValidator.ValidateChange(policy, CatalogChange.Between(before, policy))).Path);
    }

    [Fact]
    public void Only_a_change_of_counts_is_validated_by_part()
    {
        var before = Catalog(Schema(null, Table("bars", 100)));
        var after = Catalog(Schema(null, Table("bars", 100, columns: [.. Columns])));

        Assert.Throws<ArgumentException>(
            () => CatalogValidator.ValidateChange(after, CatalogChange.Between(before, after)));
    }
}
