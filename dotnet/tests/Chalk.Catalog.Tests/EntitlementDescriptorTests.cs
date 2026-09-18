using Chalk.Ir;
using IrDisclosure = Chalk.Ir.Disclosure;
using IrEnforcement = Chalk.Ir.Enforcement;

using Chalk.Entitlements;
using Disclosure = Chalk.Entitlements.Disclosure;
using DisclosureRule = Chalk.Entitlements.DisclosureRule;
using Enforcement = Chalk.Entitlements.Enforcement;
using TestShape = Chalk.Entitlements.TestShape;

namespace Chalk.Catalog.Tests;

/// <summary>
/// Layer A's descriptor: its shape, its content hash and the round trip to the wire
/// (<c>docs/design/16-entitlements.md</c> §1). The rules that need a SQL parser are the sidecar's;
/// what is checked here is structure.
/// </summary>
public sealed class EntitlementDescriptorTests
{
    private static ColumnDescriptor Col(string name, ChalkType type) => new() { Name = name, Type = type };

    private static TableDescriptor Members(TableEntitlementDescriptor? entitlement, bool isPublic = false) => new()
    {
        Name = "members",
        RowCount = 12,
        IsPublic = isPublic,
        Entitlement = entitlement,
        Columns =
        [
            Col("id", ChalkType.Int32()),
            Col("org_id", ChalkType.Int32()),
            Col("first_name", ChalkType.String()),
            Col("national_id", ChalkType.String()),
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

    private static TableEntitlementDescriptor Simple(params ColumnEntitlementDescriptor[] columns) => new()
    {
        RowPredicate = "org_id IN (SELECT tenant_id FROM @ctx.grants)",
        Columns = columns,
    };

    // ---- the content hash ----

    [Fact]
    public void Two_equal_descriptors_hash_the_same()
    {
        var left = Simple(Masked(2));
        var right = Simple(Masked(2));

        Assert.Equal(left.DescriptorHash, right.DescriptorHash);
        Assert.Equal(32, left.DescriptorHash.Length);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("enforcement")]
    [InlineData("default")]
    [InlineData("push-masks")]
    [InlineData("mask")]
    [InlineData("placeholder")]
    [InlineData("floor")]
    [InlineData("aggregates")]
    [InlineData("column")]
    [InlineData("rule-when")]
    [InlineData("rule-then")]
    [InlineData("rule-mask")]
    [InlineData("rule-count")]
    [InlineData("rule-order")]
    [InlineData("otherwise")]
    [InlineData("statistical")]
    public void Changing_any_field_changes_the_hash(string field)
    {
        var baseline = new TableEntitlementDescriptor
        {
            RowPredicate = "org_id = @ctx.org",
            Columns = [BaselineColumn("none")],
        };

        var changed = field switch
        {
            "row" => new TableEntitlementDescriptor
            {
                RowPredicate = "org_id = @ctx.other",
                Columns = baseline.Columns,
            },
            "enforcement" => new TableEntitlementDescriptor
            {
                RowPredicate = baseline.RowPredicate,
                Enforcement = Enforcement.Local,
                Columns = baseline.Columns,
            },
            "default" => new TableEntitlementDescriptor
            {
                RowPredicate = baseline.RowPredicate,
                DefaultDisclosure = Disclosure.None,
                Columns = baseline.Columns,
            },
            "push-masks" => new TableEntitlementDescriptor
            {
                RowPredicate = baseline.RowPredicate,
                PushMasks = true,
                Columns = baseline.Columns,
            },
            _ => new TableEntitlementDescriptor
            {
                RowPredicate = baseline.RowPredicate,
                Columns = [BaselineColumn(field)],
            },
        };

        Assert.NotEqual(baseline.DescriptorHash, changed.DescriptorHash);
    }

    /// <summary>
    /// The baseline column, with exactly one field moved. Every field of the canonical form is one
    /// case of the theory above, so a field left out of the hash fails a test rather than quietly
    /// letting two policies share a plan.
    /// </summary>
    private static ColumnEntitlementDescriptor BaselineColumn(string moved) => new()
    {
        Column = moved == "column" ? 3 : 2,
        Mask = moved == "mask" ? "'####'" : "'****'",
        Placeholder = moved == "placeholder" ? "'-'" : "''",
        MinGroupSize = moved == "floor" ? 7 : 5,
        AggregateOnlyFunctions = moved == "aggregates" ? ["SUM"] : ["COUNT"],
        Otherwise = moved == "otherwise" ? Disclosure.None : Disclosure.Masked,
        Statistical = moved == "statistical",
        Rules = moved switch
        {
            "rule-count" => [Rule("@ctx.role = 'manager'", Disclosure.Full)],
            "rule-order" =>
            [
                Rule("@ctx.role = 'auditor'", Disclosure.Masked, "FINGERPRINT(first_name, @ctx.key)"),
                Rule("@ctx.role = 'manager'", Disclosure.Full),
            ],
            _ =>
            [
                Rule(
                    moved == "rule-when" ? "@ctx.role = 'owner'" : "@ctx.role = 'manager'",
                    moved == "rule-then" ? Disclosure.None : Disclosure.Full),
                Rule(
                    "@ctx.role = 'auditor'",
                    Disclosure.Masked,
                    moved == "rule-mask" ? "'token'" : "FINGERPRINT(first_name, @ctx.key)"),
            ],
        },
    };

    private static DisclosureRule Rule(string when, Disclosure then, string mask = "") =>
        new() { When = when, Then = then, Mask = mask };

    /// <summary>A column masked for every row: one rule that always holds, and a mask.</summary>
    private static ColumnEntitlementDescriptor Masked(int column, string mask = "'****'") => new()
    {
        Column = column,
        Rules = [Rule("TRUE", Disclosure.Masked)],
        Mask = mask,
    };

    /// <summary>A column disclosed to nobody: no rules at all, and the default None.</summary>
    private static ColumnEntitlementDescriptor Withheld(int column) => new() { Column = column };

    /// <summary>
    /// The canonical form is length-prefixed, so two descriptors cannot spell the same bytes by
    /// running one field's text into the next's.
    /// </summary>
    [Fact]
    public void Fields_cannot_run_into_one_another()
    {
        var left = new TableEntitlementDescriptor
        {
            RowPredicate = "a",
            Columns = [new ColumnEntitlementDescriptor { Column = 0, Rules = [Rule("bc", Disclosure.Full)] }],
        };
        var right = new TableEntitlementDescriptor
        {
            RowPredicate = "ab",
            Columns = [new ColumnEntitlementDescriptor { Column = 0, Rules = [Rule("c", Disclosure.Full)] }],
        };

        Assert.NotEqual(left.DescriptorHash, right.DescriptorHash);
    }

    // ---- the wire ----

    /// <summary>
    /// The first of §0's zero-cost properties, at this level: the field is absent rather than
    /// default-valued, so it contributes no tag and no length — not one byte — to a catalog whose
    /// tables carry no entitlement. (That every recorded plan and digest is unchanged is the same
    /// property one level up, and the corpus asserts it there.)
    /// </summary>
    [Fact]
    public void A_table_without_an_entitlement_adds_no_bytes()
    {
        var without = CatalogSerialization.ToProto(Catalog(Members(entitlement: null)));
        var with = CatalogSerialization.ToProto(Catalog(Members(new TableEntitlementDescriptor())));

        Assert.Null(without.Schemas[0].Tables[0].Entitlement);
        Assert.NotNull(with.Schemas[0].Tables[0].Entitlement);

        // The empty entitlement is the smallest one there is, and it still costs bytes; the absent
        // one costs none, which is the difference the test exists to pin.
        Assert.True(with.CalculateSize() > without.CalculateSize());
        Assert.Equal(
            without.CalculateSize(),
            CatalogSerialization.ToProto(Catalog(new TableDescriptor
            {
                Name = "members",
                RowCount = 12,
                Columns = Members(entitlement: null).Columns,
            })).CalculateSize());
    }

    [Fact]
    public void The_descriptor_survives_the_round_trip()
    {
        var entitlement = new TableEntitlementDescriptor
        {
            RowPredicate = "org_id IN (SELECT tenant_id FROM @ctx.grants)",
            Enforcement = Enforcement.Local,
            DefaultDisclosure = Disclosure.None,
            PushMasks = true,
            Columns =
            [
                new ColumnEntitlementDescriptor
                {
                    Column = 2,
                    Rules =
                    [
                        Rule("@ctx.role = 'manager'", Disclosure.Full),
                        Rule("@ctx.role = 'auditor'", Disclosure.Masked, "FINGERPRINT(first_name, @ctx.key)"),
                        Rule("TRUE", Disclosure.AggregateOnly),
                    ],
                    Otherwise = Disclosure.None,
                    Statistical = true,
                    Mask = "SUBSTRING(first_name, 1, 1)",
                    Placeholder = "'-'",
                    MinGroupSize = 7,
                    AggregateOnlyFunctions = ["COUNT", "SUM"],
                },
            ],
        };

        var message = CatalogSerialization.ToProto(Catalog(Members(entitlement)));
        var back = CatalogSerialization.FromProto(message).Schemas[0].Tables[0].Entitlement;

        Assert.NotNull(back);
        Assert.Equal(entitlement.RowPredicate, back.RowPredicate);
        Assert.Equal(Enforcement.Local, back.Enforcement);
        Assert.Equal(Disclosure.None, back.DefaultDisclosure);
        Assert.True(back.PushMasks);
        Assert.Equal(entitlement.DescriptorHash, back.DescriptorHash);
        var column = Assert.Single(back.Columns);
        Assert.Equal(2, column.Column);
        Assert.Equal("SUBSTRING(first_name, 1, 1)", column.Mask);
        Assert.Equal("'-'", column.Placeholder);
        Assert.Equal(7, column.MinGroupSize);
        Assert.Equal(["COUNT", "SUM"], column.AggregateOnlyFunctions);
        Assert.Equal(Disclosure.None, column.Otherwise);
        Assert.True(column.Statistical);
        Assert.Equal(3, column.Rules.Count);
        Assert.Equal("@ctx.role = 'manager'", column.Rules[0].When);
        Assert.Equal(Disclosure.Full, column.Rules[0].Then);
        Assert.Equal("", column.Rules[0].Mask);
        Assert.Equal(Disclosure.Masked, column.Rules[1].Then);
        Assert.Equal("FINGERPRINT(first_name, @ctx.key)", column.Rules[1].Mask);
        Assert.Equal(Disclosure.AggregateOnly, column.Rules[2].Then);
    }

    /// <summary>The hash travels, so the sidecar never recomputes it and cannot disagree.</summary>
    [Fact]
    public void The_hash_is_on_the_wire()
    {
        var entitlement = Simple(Withheld(2));
        var message = CatalogSerialization.ToProto(Catalog(Members(entitlement)));

        Assert.Equal(
            entitlement.DescriptorHash,
            message.Schemas[0].Tables[0].Entitlement.DescriptorHash);
    }

    [Fact]
    public void The_enums_agree_with_the_wire_ones()
    {
        Assert.Equal((int)IrDisclosure.Full, (int)Disclosure.Full);
        Assert.Equal((int)IrDisclosure.Masked, (int)Disclosure.Masked);
        Assert.Equal((int)IrDisclosure.AggregateOnly, (int)Disclosure.AggregateOnly);
        Assert.Equal((int)IrDisclosure.None, (int)Disclosure.None);
        Assert.Equal((int)IrEnforcement.Pushdown, (int)Enforcement.Pushdown);
        Assert.Equal((int)IrEnforcement.Local, (int)Enforcement.Local);
        Assert.Equal((int)IrEnforcement.PushdownRequired, (int)Enforcement.PushdownRequired);
    }

    // ---- validation ----

    [Fact]
    public void A_column_index_past_the_end_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(Withheld(9))))));

        Assert.Contains("out of range", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_rule_per_column()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(Withheld(2), Masked(2))))));

        Assert.Contains("entitled twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reachable_masked_disclosure_needs_a_mask()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("TRUE", Disclosure.Masked)],
            })))));

        Assert.Contains("nothing to put in the value's place", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The rule's own mask satisfies it; the column need declare none.</summary>
    [Fact]
    public void A_rule_may_carry_its_own_mask()
    {
        CatalogValidator.Validate(Catalog(Members(Simple(new ColumnEntitlementDescriptor
        {
            Column = 2,
            Rules = [Rule("@ctx.role = 'auditor'", Disclosure.Masked, "FINGERPRINT(first_name, @ctx.key)")],
        }))));
    }

    [Fact]
    public void A_reachable_aggregate_only_disclosure_needs_an_aggregate()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("@ctx.role = 'auditor'", Disclosure.AggregateOnly)],
            })))));

        Assert.Contains("every use of the column would be refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mask_no_rule_could_reach_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("@ctx.role = 'manager'", Disclosure.Full)],
                Mask = "'****'",
            })))));

        Assert.Contains("could never be reached", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A per-rule mask under anything but MASKED means something the descriptor cannot say.</summary>
    [Fact]
    public void A_rule_mask_on_a_rule_that_is_not_masked_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("@ctx.role = 'manager'", Disclosure.Full, "'****'")],
            })))));

        Assert.Contains("belongs on a Masked rule", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_with_an_empty_condition_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("  ", Disclosure.Full)],
            })))));

        Assert.Contains("rule condition is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_that_discloses_nothing_nameable_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("TRUE", Disclosure.Unspecified)],
            })))));

        Assert.Contains(
            "Say Full, Masked, AggregateOnly, Test or None", ex.Message, StringComparison.Ordinal);
    }

    // ---- D208: a tenanted table defaults to None ----

    /// <summary>
    /// The optimiser moves the statement's own predicates below the row filter and evaluates them on
    /// every raw row, so what a row outside the tenancy resolves to must not be its value (§3.9).
    /// </summary>
    [Theory]
    [InlineData(Disclosure.Full)]
    [InlineData(Disclosure.Masked)]
    [InlineData(Disclosure.AggregateOnly)]
    public void Otherwise_must_be_none_on_a_table_with_a_row_predicate(Disclosure otherwise)
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("@ctx.role = 'manager'", Disclosure.Full)],
                Otherwise = otherwise,
                Mask = "'****'",
                AggregateOnlyFunctions = ["COUNT"],
            })))));

        Assert.Contains("must default to None", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A table entitled only in its columns has no row predicate, and may default to Full.</summary>
    [Fact]
    public void Otherwise_may_be_full_on_a_table_without_a_row_predicate()
    {
        CatalogValidator.Validate(Catalog(Members(new TableEntitlementDescriptor
        {
            Columns =
            [
                new ColumnEntitlementDescriptor
                {
                    Column = 2,
                    Rules = [Rule("@ctx.role = 'auditor'", Disclosure.Masked)],
                    Otherwise = Disclosure.Full,
                    Mask = "'****'",
                },
            ],
        })));
    }

    // ---- D190: the permitted population aggregates ----

    [Theory]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("ANY_VALUE")]
    [InlineData("LISTAGG")]
    [InlineData("ARRAY_AGG")]
    [InlineData("PERCENTILE_CONT")]
    [InlineData("my_udaf")]
    public void An_aggregate_that_reports_an_individual_row_is_refused(string function)
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("@ctx.role = 'auditor'", Disclosure.AggregateOnly)],
                AggregateOnlyFunctions = [function],
            })))));

        Assert.Contains("is not a population aggregate", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("COUNT")]
    [InlineData("sum")]
    [InlineData("SUM0")]
    [InlineData("AVG")]
    [InlineData("STDDEV_POP")]
    [InlineData("VAR_SAMP")]
    [InlineData("COVAR_POP")]
    [InlineData("CORR")]
    [InlineData("REGR_SLOPE")]
    [InlineData("BOOL_AND")]
    [InlineData("APPROX_COUNT_DISTINCT")]
    public void A_population_aggregate_is_accepted(string function)
    {
        CatalogValidator.Validate(Catalog(Members(Simple(new ColumnEntitlementDescriptor
        {
            Column = 2,
            Rules = [Rule("@ctx.role = 'auditor'", Disclosure.AggregateOnly)],
            AggregateOnlyFunctions = [function],
        }))));

        Assert.True(PopulationAggregates.IsPermitted(function));
    }

    // ---- D203: statistical is an opt-in under a withheld name ----

    [Fact]
    public void Statistical_needs_a_withheld_name_to_relax()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [Rule("@ctx.role = 'manager'", Disclosure.Full)],
                Statistical = true,
            })))));

        Assert.Contains("no withheld value for it to relax", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Disclosure.Masked)]
    [InlineData(Disclosure.AggregateOnly)]
    public void Statistical_is_accepted_under_masked_or_aggregate_only(Disclosure then)
    {
        CatalogValidator.Validate(Catalog(Members(Simple(new ColumnEntitlementDescriptor
        {
            Column = 2,
            Rules = [Rule("@ctx.role = 'auditor'", then)],
            Statistical = true,
            Mask = then == Disclosure.Masked ? "'****'" : "",
            AggregateOnlyFunctions = then == Disclosure.AggregateOnly ? ["COUNT"] : [],
        }))));
    }

    [Theory]
    [InlineData(Disclosure.Masked)]
    [InlineData(Disclosure.AggregateOnly)]
    public void A_default_disclosure_that_needs_a_per_column_rule_is_refused(Disclosure disclosure)
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(new TableEntitlementDescriptor { DefaultDisclosure = disclosure }))));

        Assert.Contains("declared per column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_group_size_floor_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(
            Catalog(Members(Simple(new ColumnEntitlementDescriptor
            {
                Column = 2,
                MinGroupSize = -1,
            })))));

        Assert.Contains("min_group_size", ex.Message, StringComparison.Ordinal);
    }

    // ---- RequireEntitlements (D154) ----

    [Fact]
    public void Require_entitlements_refuses_a_table_nobody_declared()
    {
        var catalog = Catalog(Members(entitlement: null));

        CatalogValidator.Validate(catalog);

        var ex = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(catalog, new CatalogOptions { RequireEntitlements = true }));

        Assert.Contains("neither carries an entitlement nor is marked Public()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_entitlements_accepts_a_table_declared_public()
    {
        CatalogValidator.Validate(
            Catalog(Members(entitlement: null, isPublic: true)),
            new CatalogOptions { RequireEntitlements = true });
    }

    [Fact]
    public void Require_entitlements_accepts_an_entitled_table()
    {
        CatalogValidator.Validate(
            Catalog(Members(Simple())),
            new CatalogOptions { RequireEntitlements = true });
    }

    /// <summary>The marker is the host's own note and never travels.</summary>
    [Fact]
    public void Public_is_not_on_the_wire()
    {
        var message = CatalogSerialization.ToProto(Catalog(Members(entitlement: null, isPublic: true)));

        Assert.False(CatalogSerialization.FromProto(message).Schemas[0].Tables[0].IsPublic);
    }

    // ---- SupportsMaskPushdown ----

    [Fact]
    public void Mask_pushdown_on_a_scan_only_source_is_refused()
    {
        var catalog = new CatalogContext
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
                    Capabilities = new SourceCapabilities
                    {
                        QueryLanguage = QueryLanguage.None,
                        SupportsMaskPushdown = true,
                    },
                    Tables = [Members(entitlement: null)],
                },
            ],
        };

        var ex = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(catalog));

        Assert.Contains("SupportsMaskPushdown with QueryLanguage.None", ex.Message, StringComparison.Ordinal);
    }

    // ---- PUSHDOWN_REQUIRED against the source (D199) ----

    /// <summary>
    /// A constraint on where a row predicate is evaluated, declared on a table nothing evaluates it
    /// for: refused at registration, naming the table and the source kind.
    /// </summary>
    [Fact]
    public void Pushdown_required_on_a_table_the_client_scans_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(Members(Required()))));

        Assert.Contains("Enforcement.PushdownRequired on 'members'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Local with None", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same descriptor on a source that takes queries is what D199 is about.</summary>
    [Fact]
    public void Pushdown_required_on_a_source_that_takes_queries_validates()
    {
        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = "demo",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = "warehouse",
                    Name = "main",
                    Kind = SourceKind.Remote,
                    Dialect = "duckdb",
                    Capabilities = new SourceCapabilities { QueryLanguage = QueryLanguage.Sql },
                    DialectProfile = DialectProfiles.DuckDb,
                    Tables = [Members(Required())],
                },
            ],
        });
    }

    private static TableEntitlementDescriptor Required() => new()
    {
        RowPredicate = "org_id IN (@ctx.manager_orgs)",
        Enforcement = Enforcement.PushdownRequired,
    };

    // ---- D261: the test verdict and its shapes ----

    /// <summary>
    /// A column the principal may test: one rule disclosing Test and the shapes it permits
    /// (<c>docs/design/36-test-verdict.md</c> §1).
    /// </summary>
    private static ColumnEntitlementDescriptor Tested(
        int column, params TestShape[] shapes) => new()
    {
        Column = column,
        Rules =
        [
            new DisclosureRule
            {
                When = "org_id IN (@ctx.support_orgs)",
                Then = Disclosure.Test,
                Tests = shapes,
            },
        ],
    };

    [Fact]
    public void A_test_rule_with_its_shapes_validates()
    {
        CatalogValidator.Validate(Catalog(Members(Simple(
            Tested(3, TestShape.Equals, TestShape.NotEquals, TestShape.In)))));
    }

    [Fact]
    public void A_test_rule_naming_no_shape_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(Members(Simple(Tested(3))))));

        Assert.Contains("names no comparison shape", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_on_a_rule_that_discloses_the_value_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(Members(Simple(
                new ColumnEntitlementDescriptor
                {
                    Column = 3,
                    Rules =
                    [
                        new DisclosureRule
                        {
                            When = "org_id IN (@ctx.manager_orgs)",
                            Then = Disclosure.Full,
                            Tests = [TestShape.Equals],
                        },
                    ],
                })))));

        Assert.Contains("could never be reached", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unspecified_shape_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(Members(Simple(
                Tested(3, TestShape.Unspecified))))));

        Assert.Contains("not one of the three", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A LIST is the one kind Chalk's own contract states no comparison for (D58), so a rule
    /// permitting a comparison of one could never be compiled at the leaf.
    /// </summary>
    [Fact]
    public void A_test_on_a_column_whose_type_has_no_equality_is_refused()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Catalog(new TableDescriptor
            {
                Name = "members",
                RowCount = 12,
                Entitlement = Simple(Tested(2, TestShape.Equals)),
                Columns =
                [
                    Col("id", ChalkType.Int32()),
                    Col("org_id", ChalkType.Int32()),
                    Col("tags", ChalkType.List(ChalkType.String())),
                ],
            })));

        Assert.Contains("no equality", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shapes are in the canonical form, and only where there are any: a descriptor that permits
    /// no test hashes exactly as it did before the field existed, which is what keeps §0's zero-cost
    /// property true of the hash.
    /// </summary>
    [Fact]
    public void The_shapes_are_in_the_hash_and_cost_nothing_when_there_are_none()
    {
        var equals = Simple(Tested(3, TestShape.Equals));
        var both = Simple(Tested(3, TestShape.Equals, TestShape.NotEquals));
        Assert.NotEqual(equals.DescriptorHash, both.DescriptorHash);

        var withoutTests = Simple(new ColumnEntitlementDescriptor
        {
            Column = 3,
            Rules = [new DisclosureRule { When = "org_id IN (@ctx.support_orgs)", Then = Disclosure.None }],
        });
        var same = Simple(new ColumnEntitlementDescriptor
        {
            Column = 3,
            Rules =
            [
                new DisclosureRule
                {
                    When = "org_id IN (@ctx.support_orgs)",
                    Then = Disclosure.None,
                    Tests = [],
                },
            ],
        });
        Assert.Equal(withoutTests.DescriptorHash, same.DescriptorHash);
    }
}
