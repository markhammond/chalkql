using Chalk.Entitlements;
using Chalk.Ir;
using Disclosure = Chalk.Entitlements.Disclosure;

namespace Chalk.Catalog.Tests;

/// <summary>
/// Visibility derived through a parent, as much of it as the client can see
/// (<c>docs/design/16-entitlements.md</c> §3.13, D225): the descriptor field, its place in the
/// content hash, and the shape checks that need no SQL parser.
/// </summary>
public sealed class ParentVisibilityTests
{
    private static ColumnDescriptor Col(string name, ChalkType type) => new() { Name = name, Type = type };

    private static TableDescriptor Threads(TableEntitlementDescriptor? entitlement) => new()
    {
        Name = "threads",
        RowCount = 20,
        Entitlement = entitlement,
        UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
        Columns = [Col("id", ChalkType.Int32()), Col("org_id", ChalkType.Int32())],
    };

    private static TableDescriptor Messages(TableEntitlementDescriptor entitlement) => new()
    {
        Name = "messages",
        RowCount = 200,
        Entitlement = entitlement,
        UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
        Columns =
        [
            Col("id", ChalkType.Int32()),
            Col("thread_id", ChalkType.Int32()),
            Col("content", ChalkType.String()),
        ],
    };

    private static TableEntitlementDescriptor Restricted() =>
        new() { RowPredicate = "org_id IN (@ctx.manager_orgs)" };

    private static TableEntitlementDescriptor Through(
        int column = 1, string parent = "threads", int parentColumn = 0) => new()
        {
            Through =
            [
                new ParentVisibilityDescriptor
                {
                    Column = column,
                    ParentTable = parent,
                    ParentColumn = parentColumn,
                },
            ],
        };

    private static CatalogContext Catalog(params TableDescriptor[] tables) => new()
    {
        ContextId = "demo",
        Epoch = 1,
        Schemas =
        [
            new SchemaDescriptor
            {
                SourceId = "mem",
                Name = "main",
                Kind = SourceKind.Local,
                Tables = tables,
            },
        ],
    };

    private static CatalogContext Example(
        TableEntitlementDescriptor? threads = null, TableEntitlementDescriptor? messages = null) =>
        Catalog(Threads(threads ?? Restricted()), Messages(messages ?? Through()));

    [Fact]
    public void The_example_validates()
    {
        CatalogValidator.Validate(Example());
    }

    // ------------------------------------------------------------------ the content hash

    /// <summary>
    /// The relationship is part of what the descriptor <em>is</em>, so it is in the hash: a policy
    /// that changed which parent a row's visibility derives from would otherwise be the same plan.
    /// </summary>
    [Fact]
    public void The_parent_is_in_the_content_hash()
    {
        var bare = new TableEntitlementDescriptor { RowPredicate = "" };
        var through = Through();
        var otherColumn = Through(column: 0);
        var otherParent = Through(parent: "chats");

        Assert.NotEqual(bare.DescriptorHash, through.DescriptorHash);
        Assert.NotEqual(through.DescriptorHash, otherColumn.DescriptorHash);
        Assert.NotEqual(through.DescriptorHash, otherParent.DescriptorHash);
        Assert.Equal(through.DescriptorHash, Through().DescriptorHash);
    }

    /// <summary>
    /// A descriptor without one hashes exactly as it did before the field existed — the parents are
    /// appended only where there are any, so zero-cost when unused holds of the hash too (§0).
    /// </summary>
    [Fact]
    public void A_descriptor_without_a_parent_hashes_as_it_did()
    {
        Assert.Equal(
            "3f83699ed1e7be5d60576bf5a595350f",
            new TableEntitlementDescriptor { RowPredicate = "org_id IN (@ctx.manager_orgs)" }
                .DescriptorHash);
    }

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void The_parent_round_trips_through_the_wire()
    {
        var catalog = Example();
        var round = CatalogSerialization.FromProto(CatalogSerialization.ToProto(catalog));
        var through = round.Schemas[0].Tables[1].Entitlement!.Through;

        Assert.Single(through);
        Assert.Equal(1, through[0].Column);
        Assert.Equal("threads", through[0].ParentTable);
        Assert.Equal(0, through[0].ParentColumn);
        Assert.Equal("", through[0].ParentSchema);
    }

    // ------------------------------------------------------------------ what the client refuses

    [Fact]
    public void A_parent_the_catalog_does_not_hold_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(messages: Through(parent: "chat_threads"))));

        Assert.Contains("main.chat_threads", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("which this catalog does not hold", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_that_carries_no_entitlement_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(Threads(null), Messages(Through()))));

        Assert.Contains("carries no entitlement", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Through an unrestricted parent restricts nothing",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_that_restricts_no_row_is_refused()
    {
        var unrestricted = new TableEntitlementDescriptor
        {
            Columns =
            [
                new ColumnEntitlementDescriptor { Column = 1, Otherwise = Disclosure.None },
            ],
        };

        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(threads: unrestricted)));

        Assert.Contains("restricts no row", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_key_that_is_not_a_declared_unique_key_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(messages: Through(parentColumn: 1))));

        Assert.Contains(
            "is not a declared unique key of the parent", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correlation_key_of_another_type_kind_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(messages: Through(column: 2))));

        Assert.Contains("same type kind", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chain_of_parents_that_returns_to_a_table_is_refused()
    {
        var cyclic = new TableEntitlementDescriptor
        {
            RowPredicate = "org_id IN (@ctx.manager_orgs)",
            Through =
            [
                new ParentVisibilityDescriptor
                {
                    Column = 0,
                    ParentTable = "messages",
                    ParentColumn = 0,
                },
            ],
        };

        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(threads: cyclic)));

        Assert.Contains("the chain of derived visibility returns to", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A diamond is two routes to one parent and is not a cycle (D225).</summary>
    [Fact]
    public void A_diamond_is_not_a_cycle()
    {
        var twoWays = new TableEntitlementDescriptor
        {
            Through =
            [
                new ParentVisibilityDescriptor { Column = 1, ParentTable = "threads", ParentColumn = 0 },
                new ParentVisibilityDescriptor { Column = 0, ParentTable = "threads", ParentColumn = 0 },
            ],
        };

        CatalogValidator.Validate(Example(messages: twoWays));
    }
}
