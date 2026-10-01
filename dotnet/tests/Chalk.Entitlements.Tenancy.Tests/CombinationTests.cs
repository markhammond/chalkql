using System.Text.RegularExpressions;
using Chalk.Catalog;
using Chalk.Sources.Poco;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// Declared combinations (<c>docs/design/59-declared-combinations.md</c>, D320): a policy that
/// declares the combinations its grants hold compiles one membership test per combination and role
/// on each route that answers it, instead of every subset — and refuses a grant of any other shape.
/// </summary>
public sealed class CombinationTests
{
    private sealed record Asset(string AssetId, string Classification, string Releasability);

    private sealed record Tasking(string TaskingId, string AssetId, string MissionId);

    private sealed record Report(
        string ReportId,
        string AreaId,
        string Classification,
        string MissionId,
        string CompartmentId,
        string Releasability,
        string Environment);

    private sealed record Observation(string ObservationId, string ReportId);

    private sealed record Twin(string TwinId, string AssetId, string ReportId);

    private static readonly CatalogContext Catalog = new()
    {
        ContextId = "combinations",
        Schemas =
        [
            new PocoSourceBuilder("k", "k")
                .AddTable("assets", Array.Empty<Asset>(), t => t.UniqueKey(x => x.AssetId))
                .AddTable("tasking", Array.Empty<Tasking>(), t => t
                    .UniqueKey(x => x.TaskingId)
                    .ForeignKey<Asset>(x => x.AssetId, to: "assets", x => x.AssetId, verify: false))
                .AddTable("reports", Array.Empty<Report>(), t => t.UniqueKey(x => x.ReportId))
                .AddTable("observations", Array.Empty<Observation>(), t => t
                    .UniqueKey(x => x.ObservationId)
                    .ForeignKey<Report>(x => x.ReportId, to: "reports", x => x.ReportId, verify: false))
                .AddTable("twins", Array.Empty<Twin>(), t => t
                    .UniqueKey(x => x.TwinId)
                    .ForeignKey<Asset>(x => x.AssetId, to: "assets", x => x.AssetId, verify: false)
                    .ForeignKey<Report>(x => x.ReportId, to: "reports", x => x.ReportId, verify: false))
                .Build()
                .DescribeSchema(),
        ],
    };

    private static readonly string[] Five = ["classification", "mission", "compartment", "releasability", "environment"];

    /// <summary>The Kestrel shape: reports with five markings, and a declaration hook.</summary>
    private sealed class Shape
    {
        internal Shape(Action<Shape>? declare = null, bool withArea = false)
        {
            Policy = TenancyPolicy.Declare(Catalog);
            Classification = Policy.Tenancy("classification");
            Mission = Policy.Tenancy("mission");
            Compartment = Policy.Tenancy("compartment");
            Releasability = Policy.Tenancy("releasability");
            Environment = Policy.Tenancy("environment");
            Area = Policy.Tenancy("area");
            Analyst = Policy.Role("analyst");
            Commander = Policy.Role("commander");
            TaskingReader = Policy.Role("tasking_reader");

            var source = Policy.Source("k");
            Reports = source.Table("reports");
            Reports.Tenancy(t =>
            {
                t.Direct(Classification, Reports.Column("Classification"))
                    .Direct(Mission, Reports.Column("MissionId"))
                    .Direct(Compartment, Reports.Column("CompartmentId"))
                    .Direct(Releasability, Reports.Column("Releasability"))
                    .Direct(Environment, Reports.Column("Environment"));
                if (withArea)
                {
                    t.Direct(Area, Reports.Column("AreaId"));
                }
            });

            declare?.Invoke(this);
        }

        internal TenancyPolicy Policy { get; }

        internal Kind Classification { get; }

        internal Kind Mission { get; }

        internal Kind Compartment { get; }

        internal Kind Releasability { get; }

        internal Kind Environment { get; }

        internal Kind Area { get; }

        internal Role Analyst { get; }

        internal Role Commander { get; }

        internal Role TaskingReader { get; }

        internal Table Reports { get; }

        internal Kind[] FiveKinds => [Classification, Mission, Compartment, Releasability, Environment];

        internal TenancyEntitlements Compile() => Policy.Compile(Catalog);
    }

    private static string[] ListsIn(string text) =>
    [
        .. Regex.Matches(text, @"@ctx\.([a-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Where(name => !name.StartsWith("global", StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal),
    ];

    // ------------------------------------------------------------------ what is compiled

    /// <summary>
    /// A policy that declares no combination compiles each set of the row's kinds once, anchored on
    /// the first of its kinds in the policy's order (F165): five kinds on one row are 2⁵ − 1 groups
    /// per role, where one per anchor was 5 × 2⁴.
    /// </summary>
    [Fact]
    public void A_policy_that_declares_none_compiles_each_set_of_kinds_once()
    {
        var shape = new Shape();

        var lists = ListsIn(shape.Compile().For(shape.Reports)!.RowPredicate);

        Assert.Equal(31 * 3, lists.Length);
        Assert.Contains("classification_analyst_within_mission_compartment_releasability_environment", lists);
        Assert.Contains("mission_analyst_within_compartment_releasability", lists);
        Assert.DoesNotContain(lists, list => list.StartsWith("mission_analyst_within_classification", StringComparison.Ordinal));
        Assert.DoesNotContain("environment_analyst_within_classification_mission_compartment_releasability", lists);
    }

    /// <summary>
    /// Along a path only the path's kinds anchor a set (F165): the tasking holds its mission, and
    /// reaches the asset's classification and releasability down the same steps, so {classification,
    /// releasability} and the three-kind set are anchored on classification alone, while
    /// {releasability, mission} keeps its group on the releasability path although mission comes
    /// first — the tasking's own row cannot answer releasability.
    /// </summary>
    [Fact]
    public void Along_a_path_a_set_is_anchored_on_a_path_kind()
    {
        Table tasking = default;
        var shape = new Shape(s =>
        {
            var source = s.Policy.Source("k");
            var assets = source.Table("assets");
            tasking = source.Table("tasking");
            assets.Tenancy(t => t
                .Direct(s.Classification, assets.Column("Classification"))
                .Direct(s.Releasability, assets.Column("Releasability")));
            tasking.Tenancy(t => t
                .Direct(s.Mission, tasking.Column("MissionId"))
                .Inherited(s.Classification).Through(assets)
                .Inherited(s.Releasability).Through(assets));
        });

        var descriptor = shape.Compile().For(tasking)!;
        var lists = ListsIn(string.Join(
            " ", descriptor.Inherited.Select(p => p.EndpointPredicate + " " + p.PathPredicate)
                .Append(descriptor.RowPredicate)));

        Assert.Equal(
            [
                "classification_tasking_reader",
                "classification_tasking_reader_within_mission",
                "classification_tasking_reader_within_mission_ids",
                "classification_tasking_reader_within_mission_releasability",
                "classification_tasking_reader_within_mission_releasability_ids",
                "classification_tasking_reader_within_releasability",
                "mission_tasking_reader",
                "releasability_tasking_reader",
                "releasability_tasking_reader_within_mission",
                "releasability_tasking_reader_within_mission_ids",
            ],
            lists.Where(list => list.Contains("tasking_reader", StringComparison.Ordinal)));
    }

    /// <summary>
    /// And a grant fills its set's one list whichever kind it names first (D321): spelt from
    /// releasability, the tasking reader's three-kind grant fills the list anchored on classification,
    /// and its projection.
    /// </summary>
    [Fact]
    public void A_grant_spelt_from_any_kind_fills_its_sets_one_list()
    {
        var shape = new Shape(s =>
        {
            var source = s.Policy.Source("k");
            var assets = source.Table("assets");
            var tasking = source.Table("tasking");
            assets.Tenancy(t => t
                .Direct(s.Classification, assets.Column("Classification"))
                .Direct(s.Releasability, assets.Column("Releasability")));
            tasking.Tenancy(t => t
                .Direct(s.Mission, tasking.Column("MissionId"))
                .Inherited(s.Classification).Through(assets)
                .Inherited(s.Releasability).Through(assets));
        });

        var bound = shape.Compile().Bind(new TenancyPrincipal
        {
            User = 1,
            Grants =
            [
                Grant.ForTenancy(shape.Releasability, "REL_FVEY", shape.TaskingReader)
                    .Within(shape.Mission, "KESTREL")
                    .Within(shape.Classification, "SECRET"),
            ],
        });

        Assert.Equal(
            ["classification_tasking_reader_within_mission_releasability", "classification_tasking_reader_within_mission_releasability_ids"],
            bound.Lists.Where(l => l.Value.Rows.Count > 0).Select(l => l.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["SECRET", "KESTREL", "REL_FVEY"],
            bound.Lists["classification_tasking_reader_within_mission_releasability"].Rows.Single());
    }

    /// <summary>
    /// A declaring policy compiles its combinations and nothing else: one test per combination and
    /// admitted role, where the undeclared policy wrote 160 of them per role.
    /// </summary>
    [Fact]
    public void A_declaring_policy_compiles_one_group_per_combination_per_role()
    {
        var shape = new Shape(s => s.Policy.Combination([s.Analyst, s.Commander], s.FiveKinds));

        var lists = ListsIn(shape.Compile().For(shape.Reports)!.RowPredicate);

        Assert.Equal(
            [
                "classification_analyst_within_mission_compartment_releasability_environment",
                "classification_commander_within_mission_compartment_releasability_environment",
            ],
            lists);
    }

    /// <summary>
    /// A single kind is a combination like any other: declared, it compiles the bare list, and
    /// nothing else of that kind does.
    /// </summary>
    [Fact]
    public void A_single_kind_is_a_combination_too()
    {
        var shape = new Shape(s => s.Policy
            .Combination([s.Analyst], s.FiveKinds)
            .Combination([s.Analyst], s.Releasability));

        var lists = ListsIn(shape.Compile().For(shape.Reports)!.RowPredicate);

        Assert.Equal(
            [
                "classification_analyst_within_mission_compartment_releasability_environment",
                "releasability_analyst",
            ],
            lists);
    }

    /// <summary>
    /// The four-kind cap bounds the subsets and nothing else: a declared combination of six kinds
    /// compiles where every subset of them is refused.
    /// </summary>
    [Fact]
    public void The_four_kind_cap_does_not_bound_a_declaring_policy()
    {
        var refused = Assert.Throws<CatalogValidationException>(() => new Shape(withArea: true).Compile());
        Assert.Contains("4 is the most it will write", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ChalkErrorCodes.InvalidPolicy, refused.Code);

        var shape = new Shape(
            s => s.Policy.Combination([s.Analyst], [.. s.FiveKinds, s.Area]),
            withArea: true);

        Assert.Single(ListsIn(shape.Compile().For(shape.Reports)!.RowPredicate));
    }

    /// <summary>
    /// A combination read across a path is one cross-row group, anchored on the first path that
    /// answers it; the second path down the same steps reaches the same row and adds nothing.
    /// </summary>
    [Fact]
    public void A_combination_across_a_path_is_one_cross_row_group()
    {
        Table tasking = default;
        var shape = new Shape(s =>
        {
            var source = s.Policy.Source("k");
            var assets = source.Table("assets");
            tasking = source.Table("tasking");
            assets.Tenancy(t => t
                .Direct(s.Classification, assets.Column("Classification"))
                .Direct(s.Releasability, assets.Column("Releasability")));
            tasking.Tenancy(t => t
                .Direct(s.Mission, tasking.Column("MissionId"))
                .Inherited(s.Classification).Through(assets)
                .Inherited(s.Releasability).Through(assets));
            s.Policy.Combination([s.TaskingReader], s.Classification, s.Releasability, s.Mission);
        });

        var paths = shape.Compile().For(tasking)!.Inherited;

        var decided = Assert.Single(paths, p => p.PathPredicate.Length > 0);
        Assert.Equal(
            ["classification_tasking_reader_within_releasability_mission"],
            ListsIn(decided.PathPredicate));
        Assert.Contains(
            "(assets.Classification, assets.Releasability, MissionId) IN",
            decided.PathPredicate,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A combination whose kinds a table declares but no one route answers is refused where the
    /// policy compiles, naming the table: the grant would otherwise bind and reach nothing there.
    /// </summary>
    [Fact]
    public void A_combination_the_table_declares_without_a_route_is_refused_at_compile()
    {
        var error = Assert.Throws<CatalogValidationException>(() => new Shape(s =>
        {
            var source = s.Policy.Source("k");
            var assets = source.Table("assets");
            var twins = source.Table("twins");
            assets.Tenancy(t => t.Direct(s.Releasability, assets.Column("Releasability")));
            twins.Tenancy(t => t
                .Inherited(s.Releasability).Through(assets)
                .Inherited(s.Compartment).Through(s.Reports));
            s.Policy.Combination([s.Analyst], s.Releasability, s.Compartment);
        }).Compile());

        Assert.Contains(
            "the combination {releasability, compartment} declared for role 'analyst' resolves on 'twins'",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("no one route answers it", error.Message, StringComparison.Ordinal);
        Assert.Equal(ChalkErrorCodes.UnansweredCombination, error.Code);
        Assert.EndsWith(" [UnansweredCombination]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A combination with a kind the table does not declare is not that table's.</summary>
    [Fact]
    public void A_combination_with_a_kind_the_table_does_not_declare_is_passed_over()
    {
        Table assets = default;
        var shape = new Shape(s =>
        {
            assets = s.Policy.Source("k").Table("assets");
            assets.Tenancy(t => t
                .Direct(s.Classification, assets.Column("Classification"))
                .Direct(s.Releasability, assets.Column("Releasability")));
            s.Policy.Combination([s.Analyst], s.FiveKinds);
        });

        Assert.Empty(ListsIn(shape.Compile().For(assets)!.RowPredicate));
    }

    // ------------------------------------------------------------------ what binds

    /// <summary>
    /// Either spelling of a declared combination fills its one group, the identifiers in the
    /// group's own column order.
    /// </summary>
    [Fact]
    public void Either_spelling_fills_the_declared_group()
    {
        var shape = new Shape(s => s.Policy.Combination([s.Analyst], s.FiveKinds));
        var compiled = shape.Compile();

        foreach (var grant in new[]
        {
            Grant.ForTenancy(shape.Classification, "SECRET", shape.Analyst)
                .Within(shape.Mission, "KESTREL").Within(shape.Compartment, "ORCHID")
                .Within(shape.Releasability, "REL_FVEY").Within(shape.Environment, "SECURE"),
            Grant.ForTenancy(shape.Releasability, "REL_FVEY", shape.Analyst)
                .Within(shape.Environment, "SECURE").Within(shape.Classification, "SECRET")
                .Within(shape.Compartment, "ORCHID").Within(shape.Mission, "KESTREL"),
        })
        {
            var bound = compiled.Bind(new TenancyPrincipal { User = 1, Grants = [grant] });
            Assert.Equal(
                [["SECRET", "KESTREL", "ORCHID", "REL_FVEY", "SECURE"]],
                bound.Lists["classification_analyst_within_mission_compartment_releasability_environment"]
                    .Rows.Select(r => r.ToArray()));
        }
    }

    /// <summary>
    /// A grant of any other shape is refused at binding, naming the combinations its role declares:
    /// this is what closes F158's silent case — a grant either is a declared combination, and is
    /// answered wherever that has a route, or is refused.
    /// </summary>
    [Fact]
    public void A_grant_of_an_undeclared_shape_is_refused_naming_the_roles_combinations()
    {
        var shape = new Shape(s => s.Policy.Combination([s.Analyst], s.FiveKinds));
        var compiled = shape.Compile();

        var error = Assert.Throws<CatalogValidationException>(() => compiled.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants =
            [
                Grant.ForTenancy(shape.Classification, "SECRET", shape.Analyst)
                    .Within(shape.Releasability, "REL_FVEY"),
            ],
        }));

        Assert.Contains(
            "the grant holds {classification, releasability} in role 'analyst', which is no combination "
            + "this policy declares for 'analyst' — it declares "
            + "{classification, mission, compartment, releasability, environment}",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(ChalkErrorCodes.UndeclaredCombination, error.Code);
        Assert.EndsWith(" [UndeclaredCombination]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One kind alone is a shape as well, and refused unless declared: the lone marking that reached
    /// every row carrying it, in every mission and compartment.
    /// </summary>
    [Fact]
    public void A_single_kind_grant_is_refused_unless_declared()
    {
        var lone = (Shape s) => new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(s.Releasability, "REL_FVEY", s.Analyst)],
        };

        var strict = new Shape(s => s.Policy.Combination([s.Analyst], s.FiveKinds));
        Assert.Throws<CatalogValidationException>(() => strict.Compile().Bind(lone(strict)));

        var declared = new Shape(s => s.Policy
            .Combination([s.Analyst], s.FiveKinds)
            .Combination([s.Analyst], s.Releasability));
        var bound = declared.Compile().Bind(lone(declared));
        Assert.Equal([["REL_FVEY"]], bound.Lists["releasability_analyst"].Rows.Select(r => r.ToArray()));
    }

    /// <summary>A role the policy declares no combination for holds no tenancy grant at all.</summary>
    [Fact]
    public void A_role_with_no_combination_holds_no_tenancy_grant()
    {
        var shape = new Shape(s => s.Policy.Combination([s.Analyst], s.FiveKinds));

        var error = Assert.Throws<CatalogValidationException>(() => shape.Compile().Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(shape.Classification, "SECRET", shape.Commander)],
        }));

        Assert.Contains("it declares none for that role", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the declaration

    [Fact]
    public void A_combination_names_a_role_and_a_kind()
    {
        var shape = new Shape();

        Assert.Contains(
            "names no role",
            Assert.Throws<CatalogValidationException>(() => shape.Policy.Combination([], shape.Mission)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "names no kind",
            Assert.Throws<CatalogValidationException>(() => shape.Policy.Combination([shape.Analyst])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_combination_is_a_set_of_kinds()
    {
        var shape = new Shape();

        var error = Assert.Throws<CatalogValidationException>(
            () => shape.Policy.Combination([shape.Analyst], shape.Mission, shape.Mission));

        Assert.Contains("names kind 'mission' twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_marker_role_holds_no_combination()
    {
        var shape = new Shape();

        var error = Assert.Throws<CatalogValidationException>(
            () => shape.Policy.Combination([Roles.Visible], shape.Mission));

        Assert.Contains("rather than a role a grant is held in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_handle_of_another_policy_is_refused()
    {
        var shape = new Shape();
        var other = new Shape();

        Assert.Throws<CatalogValidationException>(
            () => shape.Policy.Combination([shape.Analyst], other.Mission));
        Assert.Throws<CatalogValidationException>(
            () => shape.Policy.Combination([other.Analyst], shape.Mission));
    }

    /// <summary>Declared twice, in either order, is declared once.</summary>
    [Fact]
    public void A_combination_declared_twice_is_declared_once()
    {
        var shape = new Shape(s => s.Policy
            .Combination([s.Analyst], s.Classification, s.Mission)
            .Combination([s.Analyst], s.Mission, s.Classification)
            .Combination([s.Analyst], s.FiveKinds));

        var lists = ListsIn(shape.Compile().For(shape.Reports)!.RowPredicate);

        Assert.Equal(2, lists.Length);
        Assert.Contains("classification_analyst_within_mission", lists);
        _ = Five;
    }
}
