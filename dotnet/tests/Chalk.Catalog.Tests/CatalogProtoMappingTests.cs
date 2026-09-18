using Chalk.Ir;
using Google.Protobuf;

namespace Chalk.Catalog.Tests;

public sealed class CatalogProtoMappingTests
{
    private static CatalogContext Sample() => new()
    {
        ContextId = "demo",
        Epoch = 7,
        Schemas =
        [
            new SchemaDescriptor
            {
                SourceId = "mem",
                Name = "main",
                Kind = SourceKind.Local,
                Tables =
                [
                    new TableDescriptor
                    {
                        Name = "bars",
                        RowCount = 100_800,
                        Columns =
                        [
                            new ColumnDescriptor { Name = "symbol", Type = ChalkType.String() },
                            new ColumnDescriptor { Name = "ts", Type = ChalkType.Timestamp(9) },
                            new ColumnDescriptor { Name = "vwap", Type = ChalkType.Decimal(28, 10, nullable: true) },
                        ],
                        UniqueKeys = [new UniqueKeyDescriptor { Columns = [1, 0] }],
                        Collations =
                        [
                            new CollationDescriptor
                            {
                                Keys =
                                [
                                    new KeyOrder(1, SortDirection.AscNullsLast),
                                    new KeyOrder(0, SortDirection.DescNullsFirst),
                                ],
                            },
                        ],
                        Indexes =
                        [
                            new IndexDescriptor
                            {
                                Name = "ix_symbol_ts",
                                Kind = IndexKind.Ordered,
                                Columns = [0, 1],
                                Unique = true,
                            },

                            // D257: the clustered kind and the covering set, both additive.
                            new IndexDescriptor
                            {
                                Name = "cx_symbol_ts",
                                Kind = IndexKind.Clustered,
                                Columns = [0, 1],
                                Unique = true,
                                Covering = [0, 1, 2],
                            },
                        ],
                    },
                ],
            },
            new SchemaDescriptor
            {
                SourceId = "sql",
                Name = "warehouse",
                Kind = SourceKind.Remote,
                Dialect = "postgresql",
                Capabilities = new SourceCapabilities
                {
                    PushablePredicates = [PredicateShape.Eq, PredicateShape.Range],
                    PushableAggregates = [AggregateFunctionId.Sum, AggregateFunctionId.Count],
                    SupportsProject = true,
                    SupportsLimit = true,
                    MaxPushdownRows = 1_000_000,
                    UnsupportedFunctions = [FunctionId.FloorTemporal],
                },
                Tables =
                [
                    new TableDescriptor
                    {
                        Name = "dim",
                        RowCount = -1,
                        Columns = [new ColumnDescriptor { Name = "id", Type = ChalkType.Int64() }],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void A_catalog_round_trips_through_the_wire_format()
    {
        var original = Sample();

        var message = original.ToProto();
        var reparsed = Ir.CatalogContext.Parser.ParseFrom(message.ToByteArray());
        var back = CatalogProtoMapping.FromProto(reparsed);

        Assert.Equal(original.ContextId, back.ContextId);
        Assert.Equal(original.Epoch, back.Epoch);
        Assert.Equal(message, back.ToProto());
    }

    [Fact]
    public void The_proto_carries_statistics_the_planner_needs()
    {
        var message = Sample().ToProto();
        var bars = message.Schemas[0].Tables[0];

        Assert.Equal(100_800, bars.RowCount);
        Assert.Equal([1u, 0u], bars.UniqueKeys[0].Columns);
        Assert.Equal(SortDirection.AscNullsLast, bars.Collations[0].Keys[0].Direction);
        Assert.Equal(SortDirection.DescNullsFirst, bars.Collations[0].Keys[1].Direction);
        Assert.Equal(28u, bars.Columns[2].Type.Precision);
        Assert.Equal(10u, bars.Columns[2].Type.Scale);
        Assert.True(bars.Columns[2].Type.Nullable);
    }

    /// <summary>D257: the kind and the covering ordinals travel, and the ordered index carries none.</summary>
    [Fact]
    public void The_clustered_kind_and_its_covering_set_travel_on_the_wire()
    {
        var message = Sample().ToProto();
        var bars = message.Schemas[0].Tables[0];

        Assert.Equal(IndexKind.Ordered, bars.Indexes[0].Kind);
        Assert.Empty(bars.Indexes[0].Covering);
        Assert.Equal(IndexKind.Clustered, bars.Indexes[1].Kind);
        Assert.Equal([0, 1, 2], bars.Indexes[1].Covering);

        var back = CatalogProtoMapping.FromProto(
            Ir.CatalogContext.Parser.ParseFrom(message.ToByteArray()));
        var index = back.Schemas[0].Tables[0].Indexes[1];

        Assert.Equal(IndexKind.Clustered, index.Kind);
        Assert.Equal([0, 1, 2], index.Covering);
        Assert.True(index.Covers([2, 0]));
        Assert.False(back.Schemas[0].Tables[0].Indexes[0].Covering.Count > 0);
    }

    [Fact]
    public void A_local_schema_sends_an_empty_dialect_and_reads_back_as_null()
    {
        var message = Sample().ToProto();

        Assert.Equal(string.Empty, message.Schemas[0].Dialect);
        Assert.Null(CatalogProtoMapping.FromProto(message).Schemas[0].Dialect);
        Assert.Equal("postgresql", CatalogProtoMapping.FromProto(message).Schemas[1].Dialect);
    }

    [Fact]
    public void Capabilities_survive_the_round_trip()
    {
        var back = CatalogProtoMapping.FromProto(Sample().ToProto()).Schemas[1].Capabilities;

        Assert.Equal([PredicateShape.Eq, PredicateShape.Range], back.PushablePredicates);
        Assert.True(back.SupportsProject);
        Assert.False(back.SupportsSort);
        Assert.Equal(1_000_000, back.MaxPushdownRows);
        Assert.Equal([FunctionId.FloorTemporal], back.UnsupportedFunctions);
    }

    [Fact]
    public void The_default_capabilities_push_nothing()
    {
        var none = SourceCapabilities.None;

        Assert.Empty(none.PushablePredicates);
        Assert.Empty(none.PushableAggregates);
        Assert.False(none.SupportsProject || none.SupportsSort || none.SupportsLimit
            || none.SupportsInnerJoin || none.SupportsOuterJoin);
        Assert.Equal(0, none.MaxPushdownRows);

        // D273 is the exception to "everything not declared is not pushed", and it is not an
        // exception to it in practice: a conditional needs a projection or a predicate to live in,
        // and this descriptor declares neither, so nothing is pushed here either.
        Assert.True(none.SupportsCase);
    }

    /// <summary>
    /// D273: the conditional carries explicit presence on the wire and <b>absent means true</b>, so
    /// a catalog recorded before the field existed — which is every one of them — reads as a source
    /// that takes a <c>CASE</c>. That is the owner's decision of 2026-09-17 and the reason the field
    /// is spelled <c>optional</c> rather than as a plain proto3 bool, whose default would have been
    /// the opposite answer.
    /// </summary>
    [Fact]
    public void A_capability_message_that_says_nothing_about_a_conditional_reads_as_true()
    {
        // A catalog as an older client would have written it: the field is cleared, so the bytes
        // carry nothing for it at all.
        var message = Catalog(new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            SupportsProject = true,
            SupportsCase = false,
        }).ToProto();
        message.Schemas[0].Capabilities.ClearSupportsCase();
        Assert.False(message.Schemas[0].Capabilities.HasSupportsCase);

        var read = CatalogProtoMapping.FromProto(
            Ir.CatalogContext.Parser.ParseFrom(message.ToByteArray()));

        Assert.True(read.Schemas[0].Capabilities.SupportsCase);
    }

    /// <summary>And a source that cannot take one says so, which survives the round trip.</summary>
    [Fact]
    public void A_source_that_cannot_take_a_conditional_says_so_on_the_wire()
    {
        var descriptor = new SourceCapabilities
        {
            QueryLanguage = QueryLanguage.Sql,
            SupportsProject = true,
            SupportsCase = false,
        };

        var message = Catalog(descriptor).ToProto().Schemas[0].Capabilities;
        Assert.True(message.HasSupportsCase);
        Assert.False(message.SupportsCase);

        var back = CatalogProtoMapping.FromProto(
            Ir.CatalogContext.Parser.ParseFrom(Catalog(descriptor).ToProto().ToByteArray()));
        Assert.False(back.Schemas[0].Capabilities.SupportsCase);
    }

    /// <summary>One remote schema carrying exactly the capabilities under test.</summary>
    private static CatalogContext Catalog(SourceCapabilities capabilities) => new()
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
                DialectProfile = DialectProfiles.Sqlite,
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

    [Fact]
    public void Chalk_type_round_trips_through_its_proto()
    {
        foreach (var type in new[]
                 {
                     ChalkType.Bool(), ChalkType.Int64(nullable: true), ChalkType.Decimal(38, 0),
                     ChalkType.Timestamp(3), ChalkType.TimestampTz(6, nullable: true), ChalkType.Uuid(),
                 })
        {
            Assert.Equal(type, ChalkType.FromProto(type.ToProto()));
        }
    }

    [Fact]
    public void Chalk_type_prints_like_the_ir_printer()
    {
        Assert.Equal("DECIMAL(28,10)?", ChalkType.Decimal(28, 10, nullable: true).ToString());
        Assert.Equal("TIMESTAMP(9)", ChalkType.Timestamp(9).ToString());
        Assert.Equal("I64", ChalkType.Int64().ToString());
    }
}
