using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// D82: a descriptor that disagrees with itself is refused at registration. Every rule here names a
/// pair that cannot both be true, because the alternative is finding it later as a wrong answer.
/// </summary>
public sealed class CapabilityValidationTests
{
    private static CatalogContext Catalog(
        SourceCapabilities capabilities, DialectProfileDescriptor? profile = null) => new()
        {
            ContextId = "demo",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = "crm",
                    Name = "public",
                    Kind = SourceKind.Remote,
                    Dialect = "sqlite",
                    Capabilities = capabilities,
                    DialectProfile = profile ?? DialectProfiles.Sqlite,
                    Tables =
                    [
                        new TableDescriptor
                        {
                            Name = "people",
                            RowCount = 10,
                            Columns = [new ColumnDescriptor { Name = "name", Type = ChalkType.String() }],
                        },
                    ],
                },
            ],
        };

    private static CatalogValidationException Invalid(CatalogContext catalog) =>
        Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(catalog));

    [Fact]
    public void A_sql_source_that_declares_what_it_can_do_validates()
    {
        CatalogValidator.Validate(Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            PushablePredicates = [PredicateShape.Eq, PredicateShape.Range, PredicateShape.And],
            SupportsProject = true,
            SupportsLimit = true,
            SupportsOffset = true,
        }));
    }

    [Fact]
    public void Pushable_work_with_no_query_language_is_refused()
    {
        var error = Invalid(Catalog(new SourceCapabilities { SupportsProject = true }));
        Assert.Contains("QueryLanguage.None", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_declares_nothing_at_all_validates()
    {
        CatalogValidator.Validate(Catalog(SourceCapabilities.None));
    }

    /// <summary>
    /// D273: a conditional is the one capability that is on unless a source says otherwise, so the
    /// validator has nothing to refuse about it — either value is a descriptor that agrees with
    /// itself, including on a source that is only ever scanned, where nothing is pushed anyway.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Either_answer_about_a_conditional_validates(bool supportsCase)
    {
        CatalogValidator.Validate(Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            PushablePredicates = [PredicateShape.Eq],
            SupportsProject = true,
            SupportsCase = supportsCase,
        }));

        CatalogValidator.Validate(Catalog(new SourceCapabilities { SupportsCase = supportsCase }));
    }

    [Fact]
    public void Offset_without_limit_is_refused()
    {
        var error = Invalid(Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            SupportsOffset = true,
        }));

        Assert.Contains("SupportsOffset without SupportsLimit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Having_without_group_by_is_refused()
    {
        var error = Invalid(Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            SupportsHaving = true,
        }));

        Assert.Contains("nothing to filter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_max_in_list_with_no_in_shape_is_refused()
    {
        var error = Invalid(Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            SupportsProject = true,
            MaxInList = 100,
        }));

        Assert.Contains("MaxInList", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_pushdown_ceiling_is_refused()
    {
        var error = Invalid(Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            SupportsProject = true,
            MaxPushdownRows = -1,
        }));

        Assert.Contains("MaxPushdownRows", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The contradiction the design names first: a LIKE shape under a locale collation.</summary>
    [Fact]
    public void A_like_shape_under_a_locale_collation_is_refused()
    {
        var error = Invalid(Catalog(
            new SourceCapabilities
            {
                QueryLanguage = QueryLanguage.Sql,
                PushablePredicates = [PredicateShape.Like],
            },
            DialectProfiles.PostgreSql));

        Assert.Contains("StringCollation.Locale", error.Message, StringComparison.Ordinal);
        Assert.Contains("D89", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_like_prefix_shape_under_a_case_insensitive_collation_is_refused()
    {
        var error = Invalid(Catalog(
            new SourceCapabilities
            {
                QueryLanguage = QueryLanguage.Sql,
                PushablePredicates = [PredicateShape.LikePrefix],
            },
            DialectProfiles.Sqlite.With(stringCollation: StringCollation.CaseInsensitive)));

        Assert.Contains("StringCollation.CaseInsensitive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sql_source_with_no_identifier_quoting_is_refused()
    {
        var error = Invalid(Catalog(
            new SourceCapabilities { QueryLanguage = QueryLanguage.Sql, SupportsProject = true },
            DialectProfileDescriptor.None));

        Assert.Contains("quotes identifiers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ir_source_needs_no_dialect_profile()
    {
        CatalogValidator.Validate(Catalog(
            new SourceCapabilities
            {
                QueryLanguage = QueryLanguage.Ir,
                PushablePredicates = [PredicateShape.Eq],
                SupportsProject = true,
            },
            DialectProfileDescriptor.None));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    [InlineData("postgresql")]
    [InlineData("ansi")]
    public void Every_shipped_preset_is_self_consistent(string name)
    {
        var profile = DialectProfiles.ByName(name);
        Assert.Equal(name, profile.Dialect);
        CatalogValidator.Validate(Catalog(
            new SourceCapabilities
            {
                QueryLanguage = QueryLanguage.Sql,
                PushablePredicates = [PredicateShape.Eq, PredicateShape.Range, PredicateShape.And],
                SupportsProject = true,
                SupportsLimit = true,
                SupportsOffset = true,
                SupportsGroupBy = true,
                SupportsHaving = true,
            },
            profile));
    }

    /// <summary>The whole profile survives the wire, field for field.</summary>
    [Fact]
    public void A_dialect_profile_round_trips_through_the_proto()
    {
        var catalog = Catalog(
            new SourceCapabilities
            {
                QueryLanguage = QueryLanguage.Sql,
                PushablePredicates = [PredicateShape.Eq, PredicateShape.In],
                PushableFunctions = [FunctionId.Upper],
                PushableAggregates = [AggregateFunctionId.Sum],
                NativeFunctions = ["risk_score"],
                SupportsProject = true,
                SupportsSort = true,
                SupportsLimit = true,
                SupportsOffset = true,
                SupportsDistinct = true,
                SupportsGroupBy = true,
                SupportsHaving = true,
                SupportsInnerJoin = true,
                SupportsOuterJoin = true,
                SupportsSemiAntiJoin = true,
                MaxPushdownRows = 100_000,
                MaxInList = 999,
                SupportsParameters = true,
                UnsupportedFunctions = [FunctionId.Lower],
            },
            DialectProfiles.DuckDb.With(
                libraries: [SqlLibrary.Postgresql],
                timeZone: "Europe/London",
                approximateDistinctCount: true));

        var back = CatalogSerialization.FromProto(
            Ir.CatalogContext.Parser.ParseFrom(
                Google.Protobuf.MessageExtensions.ToByteArray(
                    CatalogSerialization.ToProto(catalog))));

        var schema = back.Schemas[0];
        Assert.Equal(QueryLanguage.Sql, schema.Capabilities.QueryLanguage);
        Assert.Equal([PredicateShape.Eq, PredicateShape.In], schema.Capabilities.PushablePredicates);
        Assert.Equal([FunctionId.Upper], schema.Capabilities.PushableFunctions);
        Assert.Equal(["risk_score"], schema.Capabilities.NativeFunctions);
        Assert.True(schema.Capabilities.SupportsSemiAntiJoin);
        Assert.Equal(100_000, schema.Capabilities.MaxPushdownRows);
        Assert.Equal(999, schema.Capabilities.MaxInList);
        Assert.True(schema.Capabilities.SupportsParameters);

        Assert.Equal("duckdb", schema.DialectProfile.Dialect);
        Assert.Equal([SqlLibrary.Postgresql], schema.DialectProfile.Libraries);
        Assert.Equal("Europe/London", schema.DialectProfile.TimeZone);
        Assert.True(schema.DialectProfile.ApproximateDistinctCount);
        Assert.False(schema.DialectProfile.ApproximateTopN);
        Assert.Equal(StringCollation.Binary, schema.DialectProfile.StringCollation);
        Assert.Equal(NullCollation.Last, schema.DialectProfile.DefaultNullCollation);
        Assert.Equal(6u, schema.DialectProfile.MaxTimestampPrecision);
    }
}
