using Chalk.Catalog;
using Chalk.Sources.Poco;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// Dynamic construction is the primary path (<c>docs/design/45-typed-tenancy-surface.md</c> §2,
/// D270 (b)): a host that layers its own API over Chalk maps <em>its own configuration</em> to a
/// policy at run time, and the typed surface has to be the same surface there as in a policy class.
/// </summary>
/// <remarks>
/// <para>
/// This is the owner's reason for the one thing D270 does not do: nothing here is generic over a row
/// type, because at this point in a host's startup there is no row type to name — the tables, the
/// kinds, the roles and the rules are all strings that arrived in a configuration file. A
/// <c>Table&lt;T&gt;</c> would shut this door, as informed by the user.
/// </para>
/// <para>
/// The body of <see cref="The_runtime_mapping_example_compiles_as_written"/> is §2's example
/// <b>verbatim</b>, as amended 2026-09-16 to obtain a table through its source. If the surface moves
/// under it, this test stops compiling, which is the point of having it.
/// </para>
/// </remarks>
public sealed class RuntimeMappingTests
{
    // ------------------------------------------------------------------ the host's own configuration

    private sealed record HostConfig(
        IReadOnlyList<TenancyConfig> Tenancies,
        SubjectConfig Subject,
        IReadOnlyList<RoleConfig> Roles,
        IReadOnlyList<TableConfig> Tables);

    private sealed record TenancyConfig(string Name);

    private sealed record SubjectConfig(string Name, IReadOnlyList<string> Within);

    private sealed record RoleConfig(string Name);

    private sealed record TableConfig(
        string Source,
        string Name,
        IReadOnlyList<DirectConfig> Direct,
        IReadOnlyList<RelatedConfig> Related,
        IReadOnlyList<RuleConfig> Rules);

    private sealed record DirectConfig(string Kind, string Column);

    private sealed record RelatedConfig(string Kind, IReadOnlyList<StepConfig> Steps);

    private sealed record StepConfig(string Source, string Table);

    private sealed record RuleConfig(
        string Column, IReadOnlyList<string> Roles, Verdict Verdict, string? Placeholder);

    /// <summary>
    /// What a host's configuration file says, as it would arrive: names and nothing else, with no
    /// type in the host's own code standing for any of them.
    /// </summary>
    private static HostConfig Config() => new(
        Tenancies: [new TenancyConfig("org"), new TenancyConfig("vendor")],
        Subject: new SubjectConfig("member", ["org"]),
        Roles: [new RoleConfig("manager"), new RoleConfig("vendor")],
        Tables:
        [
            new TableConfig(
                Source: "main",
                Name: "vendors",
                Direct: [new DirectConfig("vendor", "id")],
                Related: [],
                Rules: []),
            new TableConfig(
                Source: "main",
                Name: "orders",
                Direct: [new DirectConfig("org", "org_id")],
                Related:
                [
                    new RelatedConfig(
                        "vendor",
                        [new StepConfig("main", "order_items"), new StepConfig("main", "vendors")]),
                ],
                Rules:
                [
                    // The two grantees a rule may name beside the policy's roles arrive as names
                    // like any other, and the host maps one of them to the marker instance.
                    new RuleConfig("amount", ["owner"], Verdict.Full, Placeholder: null),
                    new RuleConfig("amount", ["vendor"], Verdict.None, Placeholder: "'withheld'"),
                ]),
        ]);

    // ------------------------------------------------------------------ the example

    [Fact]
    public void The_runtime_mapping_example_compiles_as_written()
    {
        var catalog = Catalog();
        var config = Config();

        // ---- design 45 §2, verbatim ------------------------------------------------------------
        var p = TenancyPolicy.Declare(catalog);
        var kinds = config.Tenancies.ToDictionary(t => t.Name, t => p.Tenancy(t.Name));
        var staff = p.Subject(config.Subject.Name, within: config.Subject.Within.Select(n => kinds[n]));
        var roles = config.Roles.ToDictionary(r => r.Name, r => p.Role(r.Name));

        foreach (var t in config.Tables)
        {
            Table table = p.Source(t.Source).Table(t.Name);
            table.Tenancy(x =>
            {
                foreach (var d in t.Direct)  x.Direct(kinds[d.Kind], table.Column(d.Column));
                foreach (var r in t.Related) x.Related(kinds[r.Kind]).Through(r.Steps.Select(s => p.Source(s.Source).Table(s.Table)));
            });
            foreach (var rule in t.Rules)
                table.Access(table.Column(rule.Column),
                             rule.Roles.Select(n => n == "owner" ? Roles.Owner : roles[n]).ToList(),
                             rule.Verdict, placeholder: rule.Placeholder is null ? null : Sql.Of(rule.Placeholder));
        }
        // ---- end of §2's example ---------------------------------------------------------------

        // `orders` names the row's creator, which is what `Roles.Owner` compares the caller with; the
        // example's loop maps the kinds and the rules and the host declares the fail-safe beside it.
        var orders = p.Source("main").Table("orders");
        orders.Tenancy(x => x.ResourceOwner(orders.Column("created_by")));

        var entitlements = p.Compile(catalog);

        // The subject kind the example declared is a handle like any other, and the grants a
        // principal holds are written in it.
        Assert.Equal("member", staff.Name);

        var descriptor = entitlements.For(orders)!;
        Assert.Contains("org_id IN (@ctx.org_manager)", descriptor.RowPredicate, StringComparison.Ordinal);

        // The `Related` path the configuration described, resolved and flattened onto the wire.
        var path = Assert.Single(descriptor.Inherited);
        Assert.Equal("vendor", path.Kind);
        Assert.Equal("vendors", path.EndpointTable);
        Assert.Collection(
            path.Steps,
            up => Assert.Equal("order_items", up.Table),
            down => Assert.Equal("vendors", down.Table));

        // The rule the configuration described, in the order it was written: the creator's own
        // access first, the vendor's placeholder second.
        var amount = descriptor.FindColumn(2)!;
        Assert.Contains(
            amount.Rules,
            rule => rule.When.Contains("created_by = @ctx.user", StringComparison.Ordinal));
        Assert.Contains(
            amount.Rules,
            rule => rule.Then == Disclosure.None
                && string.Equals(rule.Placeholder, "'withheld'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The policy-class form is the <em>same</em> surface, not a second one: the handles are the same
    /// values, held as fields rather than in a dictionary, and the policy they build is equal term for
    /// term to the one the configuration built (§2).
    /// </summary>
    [Fact]
    public void The_policy_class_form_compiles_to_the_same_descriptors()
    {
        var catalog = Catalog();

        var p = TenancyPolicy.Declare(catalog);
        var source = p.Source("main");
        var org = p.Tenancy("org");
        var vendorKind = p.Tenancy("vendor");
        p.Subject("member", within: [org]);
        var manager = p.Role("manager");
        var vendorRole = p.Role("vendor");

        var vendors = source.Table("vendors");
        var orders = source.Table("orders");
        var orderItems = source.Table("order_items");

        vendors.Tenancy(x => x.Direct(vendorKind, vendors.Column("id")));
        orders
            .Tenancy(x => x
                .Direct(org, orders.Column("org_id"))
                .Related(vendorKind).Through(orderItems).Through(vendors)
                .ResourceOwner(orders.Column("created_by")))
            .Access(orders.Column("amount"), [Roles.Owner], Verdict.Full)
            .Access(orders.Column("amount"), [vendorRole], Verdict.None, placeholder: Sql.Of("'withheld'"));

        Assert.NotNull(manager.Name);

        var fromClass = p.Compile(catalog).For(orders)!;

        var fromConfig = FromConfiguration();
        Assert.Equal(fromConfig.RowPredicate, fromClass.RowPredicate);
        Assert.Equal(fromConfig.DescriptorHash, fromClass.DescriptorHash);
    }

    private static TableEntitlementDescriptor FromConfiguration()
    {
        var catalog = Catalog();
        var config = Config();
        var p = TenancyPolicy.Declare(catalog);
        var kinds = config.Tenancies.ToDictionary(t => t.Name, t => p.Tenancy(t.Name));
        p.Subject(config.Subject.Name, within: config.Subject.Within.Select(n => kinds[n]));
        var roles = config.Roles.ToDictionary(r => r.Name, r => p.Role(r.Name));

        foreach (var t in config.Tables)
        {
            var table = p.Source(t.Source).Table(t.Name);
            table.Tenancy(x =>
            {
                foreach (var d in t.Direct)
                {
                    x.Direct(kinds[d.Kind], table.Column(d.Column));
                }

                foreach (var r in t.Related)
                {
                    x.Related(kinds[r.Kind])
                        .Through(r.Steps.Select(s => p.Source(s.Source).Table(s.Table)));
                }
            });

            foreach (var rule in t.Rules)
            {
                table.Access(
                    table.Column(rule.Column),
                    rule.Roles.Select(n => n == "owner" ? Roles.Owner : roles[n]).ToList(),
                    rule.Verdict,
                    placeholder: rule.Placeholder is null ? null : Sql.Of(rule.Placeholder));
            }
        }

        var orders = p.Source("main").Table("orders");
        orders.Tenancy(x => x.ResourceOwner(orders.Column("created_by")));
        return p.Compile(catalog).For(orders)!;
    }

    // ------------------------------------------------------------------ the schema

    private sealed record Vendor(int Id, string Name);

    private sealed record Order(int Id, int OrgId, long Amount, int CreatedBy);

    private sealed record OrderItem(int Id, int OrderId, int VendorId);

    private static CatalogContext Catalog() => new()
    {
        ContextId = "runtime-mapping",
        Epoch = 1,
        Schemas = [Schema],
    };

    private static SchemaDescriptor Schema { get; } = Build();

    private static SchemaDescriptor Build()
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("vendors", Array.Empty<Vendor>(), t =>
            t.OrderedBy(v => v.Id).UniqueKey(v => v.Id));
        builder.AddTable("orders", Array.Empty<Order>(), t =>
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("order_items", Array.Empty<OrderItem>(), t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            t.ForeignKey(i => i.OrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(i => i.VendorId).References<Vendor>(v => v.Id, verify: true);
        });
        return builder.Build().DescribeSchema();
    }
}
