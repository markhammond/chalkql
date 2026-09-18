using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 18 — advanced topics: runtime policy configuration. Everything before this chapter wrote
/// its policy as code. A host that layers its own API over Chalk does not have that: its tenancies,
/// roles, tables and rules arrive in a configuration file, as strings, and the policy has to be
/// built while the process starts. The typed surface is the same surface there.
/// </summary>
public static class Chapter18Advanced
{
    // ---------------------------------------------------------------- the host's own configuration

    /// <summary>What a host's configuration file says, as it would arrive: names, and nothing else.</summary>
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

    private static HostConfig Config() => new(
        Tenancies: [new("customer"), new("supplier"), new("region"), new("warehouse")],
        Subject: new SubjectConfig("employee", ["customer"]),
        Roles: [new("buyer"), new("representative"), new("sales")],
        Tables:
        [
            new TableConfig(
                Source: "main",
                Name: "products",
                Direct: [new DirectConfig("supplier", "supplier_id")],
                Related: [],
                Rules: []),
            new TableConfig(
                Source: "main",
                Name: "orders",
                Direct:
                [
                    new DirectConfig("customer", "customer_id"),
                    new DirectConfig("region", "region_id"),
                ],
                Related:
                [
                    new RelatedConfig(
                        "supplier",
                        [new StepConfig("main", "order_details"), new StepConfig("main", "products")]),
                ],
                Rules:
                [
                    // The two grantees a rule may name beside the policy's roles arrive as names
                    // like any other, and the host maps them to the marker instances.
                    new RuleConfig("freight", ["owner"], Verdict.Full, Placeholder: null),
                    new RuleConfig("freight", ["representative"], Verdict.None, Placeholder: "CAST(NULL AS DECIMAL(19,2))"),
                    new RuleConfig("order_date", ["visible"], Verdict.Full, Placeholder: null),
                ]),
        ]);

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            18,
            "Advanced topics: runtime policy configuration",
            "Every policy so far was written as code, with a field per name. A host that maps its\n"
            + "own configuration to a policy has no fields to write: the names arrive as strings and\n"
            + "there is no row type to be generic over. The surface is the same one, and this is what\n"
            + "it looks like when the names are data.");

        var shape = Marketplace.Poco();
        var catalog = Chapter10WhosAsking.Catalog(shape);
        var config = Config();

        Tutorial.Step("the mapping, whole");
        Console.WriteLine("""
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
                        table.Access(table.Column(rule.Column), Grantees(rule, roles), rule.Verdict,
                                     placeholder: rule.Placeholder is null ? null : Sql.Of(rule.Placeholder));
                }
            """);

        // ---- and here it is, run ---------------------------------------------------------------
        var p = TenancyPolicy.Declare(catalog);
        var kinds = config.Tenancies.ToDictionary(t => t.Name, t => p.Tenancy(t.Name));
        var staff = p.Subject(config.Subject.Name, within: config.Subject.Within.Select(n => kinds[n]));
        var roles = config.Roles.ToDictionary(r => r.Name, r => p.Role(r.Name));

        foreach (var t in config.Tables)
        {
            Table table = p.Source(t.Source).Table(t.Name);
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
                    Grantees(rule, roles),
                    rule.Verdict,
                    placeholder: rule.Placeholder is null ? null : Sql.Of(rule.Placeholder));
            }
        }

        // `orders` names the employee who handled it, which is what `Roles.Owner` compares the
        // caller with; the configuration described the kinds and the rules and the host declares the
        // fail-safe beside it.
        var orders = p.Source("main").Table("orders");
        orders.Tenancy(x => x
            .Direct(staff, orders.Column("employee_id"))
            .ResourceOwner(orders.Column("employee_id")));

        var entitlements = p.Compile(catalog);

        Tutorial.Step("what that configuration compiled to");
        var descriptor = entitlements.For(orders)!;
        Tutorial.Block("orders, row predicate", descriptor.RowPredicate);
        Console.WriteLine();
        Console.WriteLine("    inherited paths:");
        foreach (var path in descriptor.Inherited)
        {
            Console.WriteLine(
                $"      {path.Kind} -> {path.EndpointTable} via "
                + string.Join(" -> ", path.Steps.Select(s => s.Table)));
        }

        Console.WriteLine();
        Console.WriteLine("    column rules:");
        foreach (var column in descriptor.Columns)
        {
            foreach (var rule in column.Rules)
            {
                Console.WriteLine(
                    $"      column {column.Column}: {rule.Then}"
                    + (string.IsNullOrEmpty(rule.Placeholder)
                        ? string.Empty
                        : $" placeholder {rule.Placeholder}")
                    + $" when {Short(rule.When)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("    Two of those rules name a marker rather than a role. `owner` is the");
        Console.WriteLine("    row's resource owner, and the rule's condition gains");
        Console.WriteLine("    `employee_id = @ctx.user`. `visible` is everyone the table's row");
        Console.WriteLine("    predicate admits — every declared role's scope, the owner and the");
        Console.WriteLine("    global grant — and the compiler writes that predicate as the rule's");
        Console.WriteLine("    condition, so a host that means \"anyone who can see this row\" can say");
        Console.WriteLine("    so instead of listing the roles and getting it wrong later.");

        Tutorial.Step("and it is a policy like any other");
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-18",
            Sources = [Marketplace.Poco(entitlements)],
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();
        var principal = Perspectives.Principal(
            4, Grant.ForTenancy(kinds["region"], 4, roles["sales"]));

        const string Statement = """
            SELECT order_id, region_id, employee_id, order_date, freight
            FROM orders
            ORDER BY order_id
            """;

        Tutorial.Sql(Statement);
        var prepared = await entitled.PrepareAsync(Statement, entitlements.Bind(principal));
        Tutorial.Plan("plan", prepared.Plan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(prepared);
        Console.WriteLine();
        await Chapter10WhosAsking.PrintAsync(engine, prepared);

        Console.WriteLine();
        Console.WriteLine("    Nothing about that plan says the policy came from a file. The typed");
        Console.WriteLine("    surface is the surface either way; what a configuration loses is the");
        Console.WriteLine("    compiler checking the *names*, and what it keeps is everything else —");
        Console.WriteLine("    a kind where a kind belongs, a column that must come from the table it");
        Console.WriteLine("    is on, and a source a table is obtained from rather than guessed at.");
    }

    /// <summary>
    /// A rule's condition, short enough to read. The `visible` marker's condition is the table's
    /// whole row predicate — every role's scope, the owner and the global grant — which is the
    /// point of it and is also several hundred characters long.
    /// </summary>
    private static string Short(string when) =>
        when.Length <= 96 ? when : when[..96] + "… (" + when.Length + " characters)";

    /// <summary>
    /// The grantees of one rule: the policy's own roles by name, and the two markers by theirs. A
    /// host's configuration has strings and no way to hold a <see cref="Role"/>, so the mapping from
    /// its vocabulary to the markers is the host's to write — which is the point of them being
    /// instances rather than reserved names.
    /// </summary>
    private static IReadOnlyList<Role> Grantees(RuleConfig rule, IReadOnlyDictionary<string, Role> roles) =>
        [.. rule.Roles.Select(n => n switch
        {
            "owner" => Roles.Owner,
            "visible" => Roles.Visible,
            _ => roles[n],
        })];
}
