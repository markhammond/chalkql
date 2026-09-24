using static Chalk.TestKit.IrBuilder;

namespace Chalk.Ir.Tests;

/// <summary>
/// <b>I-IR-E</b> (<c>docs/design/16-entitlements.md</c> §3.10, D201), one deliberate breakage per
/// clause: a hand-built plan that says the wrong thing, refused by name before anything executes.
/// </summary>
/// <remarks>
/// The invariant is what a client re-establishes from its <em>own</em> catalog, so every test here
/// hands the validator a catalog that says <c>members</c> carries an entitlement over five columns
/// and then builds a plan that disagrees with it — which is the shape of every escape the clause
/// exists to catch, and one a planner's own report could never tell the client about.
/// </remarks>
public sealed class EntitlementsInvariantTests
{
    /// <summary>The entitled table: five columns, of which the third is masked and the fourth withheld.</summary>
    private static readonly RowType Members =
        Row(F("id", I64()), F("org_id", I64()), F("first_name", Str()), F("national_id", Str()),
            F("amount", I64()));

    private static PlanValidationOptions Catalog(
        IReadOnlyList<DisclosureOutcome>? reported = null) => new()
        {
            VerifyDigest = false,
            EntitledTables = table =>
                string.Equals(table.Table, "members", StringComparison.Ordinal) ? 5 : null,
            ReportedDisclosures = reported,
        };

    /// <summary>A read of the entitled table, with the verdicts a well-formed plan would carry.</summary>
    private static Rel EntitledRead(
        bool policyInjected = true,
        params (int Column, DisclosureOutcome Outcome)[] outcomes)
    {
        var read = new Rel
        {
            RowType = Members,
            PolicyInjected = policyInjected,
            Read = new Read
            {
                Table = new TableRef { SourceId = "mem", Schema = "main", Table = "members" },
                Projection = { 0u, 1u, 2u, 3u, 4u },
            },
        };

        var stated = outcomes.Length > 0
            ? outcomes
            :
            [
                (0, DisclosureOutcome.Full),
                (1, DisclosureOutcome.Full),
                (2, DisclosureOutcome.Masked),
                (3, DisclosureOutcome.Redacted),
                (4, DisclosureOutcome.Aggregate),
            ];
        foreach (var (column, outcome) in stated)
        {
            read.Read.Disclosures.Add(
                new ColumnDisclosure { Column = (uint)column, Outcome = outcome });
        }

        return read;
    }

    private static Plan PlanOf(Rel root)
    {
        var plan = new Plan
        {
            IrVersion = IrVersion.Current,
            ContextId = "demo",
            CatalogEpoch = 1,
            Root = root,
            OutputType = root.RowType,
        };
        return plan;
    }

    /// <summary>
    /// The read's descriptor against the report's, D231's clause: the plan in the client's hand and
    /// the report beside it have to speak about one policy.
    /// </summary>
    [Fact]
    public void A_read_compiled_under_another_descriptor_than_the_report_names_is_refused()
    {
        var read = EntitledRead();
        read.Read.DescriptorHash = "0123456789abcdef0123456789abcdef";
        var options = new PlanValidationOptions
        {
            VerifyDigest = false,
            EntitledTables = table =>
                string.Equals(table.Table, "members", StringComparison.Ordinal) ? 5 : null,
            ReportedDescriptorHashes = _ => "fedcba9876543210fedcba9876543210",
        };

        var refusal = Refused(PlanOf(read), options);

        Assert.Contains("0123456789abcdef0123456789abcdef", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("fedcba9876543210fedcba9876543210", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("do not speak about one policy", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And the same descriptor on both sides is accepted.</summary>
    [Fact]
    public void A_read_compiled_under_the_descriptor_the_report_names_is_accepted()
    {
        var read = EntitledRead(
            outcomes:
            [
                (0, DisclosureOutcome.Full),
                (1, DisclosureOutcome.Full),
                (2, DisclosureOutcome.Full),
                (3, DisclosureOutcome.Full),
                (4, DisclosureOutcome.Full),
            ]);
        read.Read.DescriptorHash = "0123456789abcdef0123456789abcdef";

        PlanValidator.Validate(
            PlanOf(read),
            new PlanValidationOptions
            {
                VerifyDigest = false,
                EntitledTables = table =>
                    string.Equals(table.Table, "members", StringComparison.Ordinal) ? 5 : null,
                ReportedDescriptorHashes = _ => "0123456789abcdef0123456789abcdef",
            });
    }

    private static InvalidPlanException Refused(Plan plan, PlanValidationOptions options) =>
        Assert.Throws<InvalidPlanException>(() => PlanValidator.Validate(plan, options));

    // ---- (a): every read of an entitled table went through the rewrite ----

    [Fact]
    public void A_read_of_an_entitled_table_without_the_rewrite_is_refused()
    {
        var plan = PlanOf(EntitledRead(policyInjected: false));

        var error = Refused(plan, Catalog());

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("without the enforcement rewrite", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And inside a pushed plan, which is where a check that only walked the top would miss it.</summary>
    [Fact]
    public void A_read_inside_a_pushed_plan_is_checked_too()
    {
        var plan = PlanOf(new Rel
        {
            RowType = Members,
            RemoteQuery = new RemoteQuery
            {
                SourceId = "warehouse",
                Dialect = "postgresql",
                QueryText = "SELECT * FROM members",
                PushedPlan = EntitledRead(policyInjected: false),
            },
        });

        Assert.Equal("I-IR-E", Refused(plan, Catalog()).Invariant);
    }

    // ---- (d): one verdict per column of the table ----

    [Fact]
    public void An_entitled_read_that_states_fewer_verdicts_than_the_table_has_columns_is_refused()
    {
        var plan = PlanOf(EntitledRead(
            policyInjected: true,
            (0, DisclosureOutcome.Full),
            (1, DisclosureOutcome.Full)));

        var error = Refused(plan, Catalog());

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("one per column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unentitled_read_that_states_verdicts_is_refused()
    {
        var read = EntitledRead();
        read.Read.Table.Table = "orders";
        var plan = PlanOf(read);

        Assert.Contains(
            "says the table has no entitlement",
            Refused(plan, Catalog()).Message,
            StringComparison.Ordinal);
    }

    // ---- (b): a population-only value reaches only a population aggregate ----

    [Fact]
    public void A_population_only_column_read_as_a_value_is_refused()
    {
        // Project(amount * 2) over a projection of the leaf, which is not the leaf's own sanitiser.
        var pass = new Rel
        {
            RowType = Row(F("amount", I64())),
            Project = new Project { Input = EntitledRead(), Exprs = { Ref(4, I64()) } },
        };
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("doubled", I64())),
            Project = new Project
            {
                Input = pass,
                Exprs = { Call(FunctionId.Multiply, I64(), Ref(0, I64()), Lit(2L)) },
            },
        });

        var error = Refused(plan, Catalog());

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("population-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_population_only_column_as_a_sort_key_is_refused()
    {
        var pass = new Rel
        {
            RowType = Row(F("amount", I64())),
            Project = new Project { Input = EntitledRead(), Exprs = { Ref(4, I64()) } },
        };
        var plan = PlanOf(new Rel
        {
            RowType = pass.RowType,
            Sort = new Sort
            {
                Input = pass,
                Fields = { new SortField { Expr = Ref(0, I64()), Direction = SortDirection.AscNullsLast } },
            },
        });

        Assert.Contains("a sort key", Refused(plan, Catalog()).Message, StringComparison.Ordinal);
    }

    // ---- (c): no root output column is a withheld or masked column itself ----

    [Fact]
    public void A_masked_column_at_the_root_is_refused()
    {
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("first_name", Str())),
            Project = new Project { Input = EntitledRead(), Exprs = { Ref(2, Str()) } },
        });

        var error = Refused(plan, Catalog());

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("redacted and unsanitised", error.Message, StringComparison.Ordinal);
    }

    // ---- (e): the report's labels and the plan's own verdicts ----

    [Fact]
    public void A_report_that_claims_more_disclosure_than_the_reads_do_is_refused()
    {
        // The plan's own read says org_id is full and first_name masked; a projection of both, with
        // a report claiming both are full.
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("org_id", I64()), F("initial", Str())),
            Project = new Project
            {
                Input = EntitledRead(),
                Exprs = { Ref(1, I64()), Call(FunctionId.Upper, Str(), Ref(2, Str())) },
            },
        });

        var error = Refused(
            plan, Catalog([DisclosureOutcome.Full, DisclosureOutcome.Full]));

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("claims more disclosure", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A report that is <em>stricter</em> than the walk stands: it is the safe direction.</summary>
    [Fact]
    public void A_report_stricter_than_the_reads_is_accepted()
    {
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("org_id", I64())),
            Project = new Project { Input = EntitledRead(), Exprs = { Ref(1, I64()) } },
        });

        PlanValidator.Validate(plan, Catalog([DisclosureOutcome.PerRow]));
    }

    // ---- (e): a CASE condition is a predicate position, never an origin of the value (F81) ----

    /// <summary>
    /// F81, and the walk's answer to it (ADR 0058 §2). A sanitiser is
    /// <c>CASE WHEN &lt;rule&gt; THEN value … ELSE placeholder END</c>, and its conditions read the
    /// columns the policy's rules name. Meeting those into the output's disclosure labelled the
    /// <em>value</em> by what its <em>rule</em> consulted: here a population-only <c>amount</c>
    /// whose rule reads a per-row <c>org_id</c>, walked to <c>PerRow</c> against a report that says
    /// <c>Aggregate</c>. The arms are what the value can be, and they are all it is.
    /// </summary>
    [Fact]
    public void A_case_condition_is_not_an_origin_of_the_value()
    {
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("total", I64(true))),
            Project = new Project
            {
                Input = EntitledRead(
                    outcomes:
                    [
                        (0, DisclosureOutcome.Full),
                        (1, DisclosureOutcome.PerRow),
                        (2, DisclosureOutcome.Masked),
                        (3, DisclosureOutcome.Redacted),
                        (4, DisclosureOutcome.Aggregate),
                    ]),
                Exprs =
                {
                    Case(
                        I64(true),
                        Null(I64(true)),
                        (Call(FunctionId.Eq, Bool(), Ref(1, I64()), Lit(1L)), Ref(4, I64()))),
                },
            },
        });

        PlanValidator.Validate(plan, Catalog([DisclosureOutcome.Aggregate]));
    }

    /// <summary>
    /// And the arms still are: a condition that discloses nothing does not launder a result arm
    /// reading a column the report claims more of than the read allows.
    /// </summary>
    [Fact]
    public void A_case_result_arm_is_still_an_origin_of_the_value()
    {
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("initial", Str(true))),
            Project = new Project
            {
                Input = EntitledRead(),
                Exprs =
                {
                    Case(
                        Str(true),
                        Null(Str(true)),
                        (Call(FunctionId.Eq, Bool(), Ref(1, I64()), Lit(1L)), Ref(2, Str()))),
                },
            },
        });

        var error = Refused(plan, Catalog([DisclosureOutcome.Full]));

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("claims more disclosure", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conditions are not thereby unchecked. A <c>CASE</c> above the leaf's own sanitiser whose
    /// condition reads a raw population-only column is refused by clause (b), which is where a
    /// predicate position's rule lives — the same rule an aggregate's <c>FILTER</c> is held to.
    /// </summary>
    [Fact]
    public void A_case_condition_over_a_population_only_column_is_still_refused()
    {
        var pass = new Rel
        {
            RowType = Row(F("amount", I64()), F("org_id", I64())),
            Project = new Project
            {
                Input = EntitledRead(), Exprs = { Ref(4, I64()), Ref(1, I64()) },
            },
        };
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("flagged", I64(true))),
            Project = new Project
            {
                Input = pass,
                Exprs =
                {
                    Case(
                        I64(true),
                        Null(I64(true)),
                        (Call(FunctionId.Gt, Bool(), Ref(0, I64()), Lit(5L)), Ref(1, I64()))),
                },
            },
        });

        var error = Refused(plan, Catalog());

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("population-only", error.Message, StringComparison.Ordinal);
        Assert.Contains("a CASE condition", error.Message, StringComparison.Ordinal);
    }

    // ---- a composite value: provenance through the call and the field access (ADR 0077) ----

    /// <summary>
    /// A field of a composite-valued call over a masked column, and the whole composite, are values
    /// of that column: the walk descends through the <c>FieldAccess</c> into the call and through
    /// the call into its argument, as it does through any call, and meets the read's verdict. So a
    /// report that labels them masked validates and one that labels them full is refused.
    /// </summary>
    [Fact]
    public void A_field_and_the_whole_composite_of_a_call_over_a_masked_column_derive_from_it()
    {
        var described = Composite(nullable: true, F("echo", Str(true)), F("len", I64()));
        var call = UserCall("main.describe", described, Ref(2, Str()));
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("echo", Str(true)), F("d", described)),
            Project = new Project { Input = EntitledRead(), Exprs = { FieldAccess(call, 0), call } },
        });

        PlanValidator.Validate(plan, Catalog([DisclosureOutcome.Masked, DisclosureOutcome.Masked]));

        foreach (var claimed in new[]
        {
            new[] { DisclosureOutcome.Full, DisclosureOutcome.Masked },
            new[] { DisclosureOutcome.Masked, DisclosureOutcome.Full },
        })
        {
            var error = Refused(plan, Catalog(claimed));
            Assert.Equal("I-IR-E", error.Invariant);
            Assert.Contains("claims more disclosure", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And a population-only column read through a composite is still a value use of the raw
    /// column: clause (b) walks through the field access and the call to the reference.
    /// </summary>
    [Fact]
    public void A_population_only_column_read_through_a_composite_field_is_refused()
    {
        var pass = new Rel
        {
            RowType = Row(F("amount", I64())),
            Project = new Project { Input = EntitledRead(), Exprs = { Ref(4, I64()) } },
        };
        var summarised = Composite(nullable: true, F("total", I64()), F("tally", I64()));
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("total", I64(true))),
            Project = new Project
            {
                Input = pass,
                Exprs = { FieldAccess(UserCall("main.summarise", summarised, Ref(0, I64())), 0) },
            },
        });

        var error = Refused(plan, Catalog());

        Assert.Equal("I-IR-E", error.Invariant);
        Assert.Contains("population-only", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// D295: a user aggregate over a population-only column is a population aggregate exactly when the
    /// client's own catalog says its host declared it <c>Population()</c> — the question clause (b)
    /// asks of the catalog for a measure, as the clause asks it which tables are entitled for a read.
    /// </summary>
    [Fact]
    public void A_user_aggregate_over_a_population_only_column_is_permitted_only_when_declared_population()
    {
        var summarised = Composite(nullable: true, F("total", I64()), F("tally", I64()));
        var plan = PlanOf(HashAggregate(
            EntitledRead(), [1], [("s", UserAgg("main.population_summary", summarised, Ref(4, I64())))]));

        var undeclared = Refused(plan, Catalog());
        Assert.Equal("I-IR-E", undeclared.Invariant);
        Assert.Contains("an aggregate that is not a population aggregate", undeclared.Message, StringComparison.Ordinal);

        var asked = new List<string>();
        PlanValidator.Validate(plan, new PlanValidationOptions
        {
            VerifyDigest = false,
            EntitledTables = table => string.Equals(table.Table, "members", StringComparison.Ordinal) ? 5 : null,
            PopulationAggregates = name =>
            {
                asked.Add(name);
                return string.Equals(name, "main.population_summary", StringComparison.Ordinal);
            },
        });

        // Asked by the name the plan spells, schema and all.
        Assert.Equal(["main.population_summary"], asked);
    }

    /// <summary>And a plan that says what it does passes, so none of the above is vacuous.</summary>
    [Fact]
    public void A_well_formed_entitled_plan_validates()
    {
        var plan = PlanOf(new Rel
        {
            RowType = Row(F("org_id", I64()), F("initial", Str())),
            Project = new Project
            {
                Input = EntitledRead(),
                Exprs = { Ref(1, I64()), Call(FunctionId.Upper, Str(), Ref(2, Str())) },
            },
        });

        PlanValidator.Validate(plan, Catalog([DisclosureOutcome.Full, DisclosureOutcome.Masked]));
    }

    /// <summary>Without a catalog the invariant does not run: there is nothing to re-establish from.</summary>
    [Fact]
    public void Without_a_catalog_the_invariant_does_not_run()
    {
        var plan = PlanOf(EntitledRead(policyInjected: false));

        PlanValidator.Validate(plan, new PlanValidationOptions { VerifyDigest = false });
    }

    // ---- an unknown relation kind fails the invariant ----

    /// <summary>
    /// Every kind the IR has is one the walk traces. A kind added later and not added here fails the
    /// invariant rather than being traced wrongly — which is the fail-closed reading — and this test
    /// is what says so at build time rather than at the first plan that meets it.
    /// </summary>
    [Fact]
    public void The_origin_walk_knows_every_relation_kind_the_ir_has()
    {
        var missing = Enum.GetValues<Rel.KindOneofCase>()
            .Where(kind => kind != Rel.KindOneofCase.None)
            .Where(kind => !EntitlementsInvariant.Traced.Contains(kind))
            .ToArray();

        Assert.Empty(missing);
    }
}
