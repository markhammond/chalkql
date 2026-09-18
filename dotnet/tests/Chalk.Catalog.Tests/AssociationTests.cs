using Google.Protobuf;
using Chalk.Entitlements;
using Chalk.Ir;
using StepDirection = Chalk.Entitlements.StepDirection;

namespace Chalk.Catalog.Tests;

/// <summary>
/// The cross-source association: what a host may declare between two tables of two sources, what
/// registration checks of it, and what a path step may resolve through
/// (<c>docs/design/45-typed-tenancy-surface.md</c> §3, D270 (c)).
/// </summary>
/// <remarks>
/// <para>
/// A foreign key is one source's claim about a table of its own schema, so the two-source layout of
/// design 38 §8 could not be declared at all (F84). An association is the same statement for a pair
/// a foreign key cannot reach, and it hangs off the catalog rather than off either schema because
/// neither source owns it.
/// </para>
/// <para>
/// The domain is the tree's neutral one: orders and their lines in one source, the vendors' items in
/// another.
/// </para>
/// </remarks>
public sealed class AssociationTests
{
    private static ColumnDescriptor Col(string name, ChalkType type) => new() { Name = name, Type = type };

    /// <summary>The target and the bridge, in the source that holds the orders.</summary>
    private static SchemaDescriptor Orders() => new()
    {
        SourceId = "duckdb",
        Name = "warehouse",
        Kind = SourceKind.Local,
        Tables =
        [
            new TableDescriptor
            {
                Name = "orders",
                RowCount = 200,
                UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
                Entitlement = new TableEntitlementDescriptor
                {
                    RowPredicate = "org_id IN (@ctx.org_manager)",
                    Inherited =
                    [
                        new InheritedVisibilityDescriptor
                        {
                            Kind = "vendor",
                            Steps =
                            [
                                new VisibilityStepDescriptor
                                {
                                    Table = "order_items",
                                    FromColumn = 0,
                                    ToColumn = 1,
                                    Direction = StepDirection.ToChild,
                                },
                                new VisibilityStepDescriptor
                                {
                                    Schema = "catalogue",
                                    Table = "items",
                                    FromColumn = 2,
                                    ToColumn = 0,
                                    Direction = StepDirection.ToParent,
                                },
                            ],
                            EndpointPredicate = "vendor_id IN (@ctx.vendor_vendor)",
                            EndpointSchema = "catalogue",
                            EndpointTable = "items",
                        },
                    ],
                },
                Columns = [Col("id", ChalkType.Int32()), Col("org_id", ChalkType.Int32())],
            },
            new TableDescriptor
            {
                Name = "order_items",
                RowCount = 900,
                UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
                ForeignKeys =
                [
                    new ForeignKeyDescriptor
                    {
                        Name = "fk_item_order",
                        Columns = [1],
                        ParentTable = "orders",
                        ParentColumns = [0],
                    },
                ],
                Columns =
                [
                    Col("id", ChalkType.Int32()),
                    Col("order_id", ChalkType.Int32()),
                    Col("item_id", ChalkType.Int32()),
                ],
            },
        ],
    };

    /// <summary>The endpoint, in the other source.</summary>
    private static SchemaDescriptor Items(ChalkType? key = null) => new()
    {
        SourceId = "sqlite",
        Name = "catalogue",
        Kind = SourceKind.Remote,
        Dialect = "sqlite",
        Tables =
        [
            new TableDescriptor
            {
                Name = "items",
                RowCount = 4_000,
                UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
                Entitlement = new TableEntitlementDescriptor
                {
                    RowPredicate = "vendor_id IN (@ctx.vendor_vendor)",
                },
                Columns =
                [
                    Col("id", key ?? ChalkType.Int32()),
                    Col("vendor_id", ChalkType.Int32()),
                ],
            },
        ],
    };

    private static AssociationDescriptor Association(string toColumn = "id") => new()
    {
        Name = "line_to_item",
        FromSchema = "warehouse",
        FromTable = "order_items",
        FromColumn = "item_id",
        ToSchema = "catalogue",
        ToTable = "items",
        ToColumn = toColumn,
    };

    private static CatalogContext Catalog(
        IReadOnlyList<AssociationDescriptor>? associations = null,
        ChalkType? key = null) => new()
        {
            ContextId = "two-sources",
            Epoch = 1,
            Schemas = [Orders(), Items(key)],
            Associations = associations ?? [Association()],
        };

    // ------------------------------------------------------------------ what a step may resolve through

    /// <summary>
    /// The step from the bridge to the endpoint crosses a source, so no foreign key can state it —
    /// and with the association declared it resolves exactly as a foreign key would (§3).
    /// </summary>
    [Fact]
    public void A_step_across_two_sources_resolves_through_a_declared_association()
    {
        CatalogValidator.Validate(Catalog());
    }

    /// <summary>
    /// Without it the same path is refused by name, which is the refusal F84 recorded: the direction
    /// is checked and never inferred, so the claim has to be declared before a path relies on it.
    /// </summary>
    [Fact]
    public void Without_the_association_the_step_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(associations: [])));

        Assert.Contains("declares no foreign key naming", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "no association between them either", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what registration checks

    /// <summary>
    /// The referenced column must be a declared unique key: an association is read exactly as a
    /// foreign key is, and the joins a path compiles into must not multiply rows.
    /// </summary>
    [Fact]
    public void The_referenced_column_must_be_a_declared_unique_key()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(associations: [Association("vendor_id")])));

        Assert.Contains("not a declared unique key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The two columns must agree in type, as a foreign key's do.</summary>
    [Fact]
    public void The_two_columns_must_agree_in_type()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(key: ChalkType.String())));

        Assert.Contains("the two types disagree", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An end the catalog does not hold is refused where it is declared.</summary>
    [Fact]
    public void An_end_this_catalog_does_not_hold_is_refused()
    {
        var missing = new AssociationDescriptor
        {
            FromSchema = "warehouse",
            FromTable = "order_items",
            FromColumn = "item_id",
            ToSchema = "catalogue",
            ToTable = "products",
            ToColumn = "id",
        };

        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(associations: [Association(), missing])));

        Assert.Contains("catalogue.products", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A column neither table holds is refused the same way.</summary>
    [Fact]
    public void A_column_the_table_does_not_hold_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(associations: [Association("sku")])));

        Assert.Contains("'sku' is not a column of", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the wire

    /// <summary>It travels with the catalog, which is what lets the sidecar resolve the same step.</summary>
    [Fact]
    public void The_association_round_trips_through_the_wire()
    {
        var round = CatalogSerialization.FromProto(CatalogSerialization.ToProto(Catalog()));

        var association = Assert.Single(round.Associations);
        Assert.Equal("line_to_item", association.Name);
        Assert.Equal("warehouse", association.FromSchema);
        Assert.Equal("order_items", association.FromTable);
        Assert.Equal("item_id", association.FromColumn);
        Assert.Equal("catalogue", association.ToSchema);
        Assert.Equal("items", association.ToTable);
        Assert.Equal("id", association.ToColumn);
    }

    /// <summary>
    /// Additive: a catalog that declares none serialises to exactly the bytes it always did, which is
    /// what keeps every recorded plan and every golden byte-identical (§4).
    /// </summary>
    [Fact]
    public void A_catalog_without_one_is_byte_identical()
    {
        var without = new CatalogContext
        {
            ContextId = "one-source",
            Epoch = 1,
            Schemas = [Items()],
        };

        Assert.Empty(without.Associations);

        // The field costs nothing when it is empty: a repeated field with no element writes no tag
        // at all, so the encoded catalog is the one it was before the field existed.
        var message = CatalogSerialization.ToProto(without);
        Assert.Empty(message.Associations);
        var bytes = message.ToByteArray();

        message.Associations.Add(new Ir.Association { FromTable = "order_items", ToTable = "items" });
        Assert.True(message.CalculateSize() > bytes.Length);

        message.Associations.Clear();
        Assert.Equal(bytes, message.ToByteArray());
    }
}
