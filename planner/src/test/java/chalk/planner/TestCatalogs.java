package chalk.planner;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.Index;
import chalk.ir.v1.IndexKind;
import chalk.ir.v1.KeyOrder;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SortDirection;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableCollation;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;

/**
 * The fixture catalogs the Java tests plan against. They mirror {@code Chalk.TestKit.Fixtures} on
 * the client side (docs/design/05-testing.md §2) — same table names, same column names, same
 * declared statistics — so a plan produced here is the plan the .NET corpus tests expect.
 */
public final class TestCatalogs {
  private TestCatalogs() {}

  public static final String CONTEXT_ID = "corpus";
  public static final long EPOCH = 1L;

  /**
   * The catalog the corpus is planned against: the one the .NET client actually pushes, read from
   * {@code corpus/schemas/corpus.binpb}.
   *
   * <p>Since M2 that catalog carries computed statistics — exact distinct counts, null counts, and a
   * min and a max per column, all derived from the generated fixture data — which cannot be written
   * out by hand in Java and kept true. So the recorded catalog is what the planner plans against,
   * and {@link #declared()} keeps the part a person actually writes down honest;
   * {@code CorpusSchemaTest} compares the two with the statistics stripped.
   */
  public static CatalogContext corpus() {
    Path recorded = CorpusQueries.corpusDir().resolve("schemas/corpus.binpb");
    if (!Files.exists(recorded)) {
      return declared();
    }
    try {
      return CatalogContext.parseFrom(Files.readAllBytes(recorded));
    } catch (IOException e) {
      throw new UncheckedIOException("reading " + recorded, e);
    }
  }

  /**
   * The shape a person declares: names, types, row counts, unique keys, collations and indexes,
   * with no computed statistics. Mirrors {@code Chalk.TestKit.CorpusFixture}.
   */
  public static CatalogContext declared() {
    return CatalogContext.newBuilder()
        .setContextId(CONTEXT_ID)
        .setEpoch(EPOCH)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                // Exactly what the client pushes for a POCO source: "scan only", and a dialect
                // profile that says nothing. CorpusSchemaTest compares the two message for message,
                // so the empty-message shorthand is not good enough — the client sends the enum.
                .setCapabilities(
                    SourceCapabilities.newBuilder()
                        .setQueryLanguage(QueryLanguage.QUERY_LANGUAGE_NONE)
                        // The client writes supports_case with explicit presence on every descriptor it pushes
                        // (ADR 0065 §2), so the recorded corpus catalog carries it and this fixture must too.
                        .setSupportsCase(true)
                        .build())
                .setDialectProfile(chalk.ir.v1.DialectProfile.getDefaultInstance())
                .addTables(bars())
                .addTables(barsSmall())
                .addTables(barsClustered())
                .addTables(symbols())
                .addTables(tradesSparse())
                .addTables(lineitem())
                .addTables(funding())
                .addTables(events())
                .addTables(customer())
                .addTables(orders())
                .addTables(nation())
                .addTables(region())
                .addTables(supplier())
                .addTables(sales())
                .addTables(sorted())
                .addTables(terms())
                .addTables(quotes())
                .addAllFunctions(functions()))
        // The client sets the message and leaves every field at its default, which is what "this
        // catalog declares no cross-source join policy" is on the wire (D104). Stated here so the
        // Java twin is byte-for-byte the catalog the client actually pushes.
        .setJoinPolicy(chalk.ir.v1.CrossSourceJoinPolicy.getDefaultInstance())
        .build();
  }

  /**
   * The functions the corpus is planned against (step 22, {@code 17-user-defined-functions.md} §5),
   * mirroring {@code Chalk.TestKit.CorpusFixture} exactly — {@code CorpusSchemaTest} compares the
   * two message for message.
   */
  public static java.util.List<chalk.ir.v1.FunctionDescriptor> functions() {
    Type d = nullable(TypeKind.TYPE_KIND_FP64);
    Type i64 = nullable(TypeKind.TYPE_KIND_I64);
    Type str = nullable(TypeKind.TYPE_KIND_STRING);
    Type ts = nullablePrecise(TypeKind.TYPE_KIND_TIMESTAMP, 9);
    return java.util.List.of(
        // SQL-bodied: inlined, so the call disappears from every plan.
        scalar("pct_change", type(TypeKind.TYPE_KIND_FP64))
            .setStrict(true)
            .addParameters(parameter("a", d))
            .addParameters(parameter("b", d))
            .setSql(chalk.ir.v1.SqlBody.newBuilder().setText("(b - a) / a"))
            .build(),
        aggregate("wavg", nullable(TypeKind.TYPE_KIND_FP64))
            .addParameters(parameter("x", d))
            .addParameters(parameter("w", d))
            .setSql(chalk.ir.v1.SqlBody.newBuilder().setText("SUM(x * w) / SUM(w)"))
            .build(),
        table(
                "bars_for",
                chalk.ir.v1.RowType.newBuilder()
                    .addFields(field("symbol", type(TypeKind.TYPE_KIND_STRING)))
                    .addFields(field("ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
                    .addFields(field("close", type(TypeKind.TYPE_KIND_FP64)))
                    .build())
            .addParameters(parameter("sym", str))
            .setSql(
                chalk.ir.v1.SqlBody.newBuilder()
                    .setText("SELECT symbol, ts, \"close\" FROM bars WHERE symbol = sym"))
            .build(),

        // Client-bodied Tier 1.
        scalar("bucket_price", type(TypeKind.TYPE_KIND_FP64))
            .setStrict(true)
            .addParameters(parameter("price", d))
            .addParameters(parameter("width", d))
            .addParameters(optional("off", d, 0.0))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        aggregate("geo_mean", nullable(TypeKind.TYPE_KIND_FP64))
            .addParameters(parameter("x", d))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        aggregate("wsum", nullable(TypeKind.TYPE_KIND_FP64))
            .setWindow(true)
            .addParameters(parameter("x", d))
            .addParameters(parameter("w", d))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        table(
                "generate_series",
                chalk.ir.v1.RowType.newBuilder()
                    .addFields(field("value", type(TypeKind.TYPE_KIND_I64)))
                    .build())
            .addParameters(parameter("start", i64))
            .addParameters(parameter("stop", i64))
            .addParameters(parameter("step", i64))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),

        // Client-bodied Tier 2: the same contract, a whole-batch kernel behind it.
        scalar("fast_hash", type(TypeKind.TYPE_KIND_I64))
            .setStrict(true)
            .addParameters(parameter("s", str))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),

        // The volatility trio, and the monotone one.
        scalar("next_seq", type(TypeKind.TYPE_KIND_I64))
            .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_VOLATILE)
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        scalar("as_of", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9))
            .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_STABLE)
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        scalar("minute_of", type(TypeKind.TYPE_KIND_I64))
            .setStrict(true)
            .addParameters(parameter("t", ts))
            .addMonotonicity(chalk.ir.v1.Monotonicity.MONOTONICITY_INCREASING)
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),

        // Structured results (D291–D294): what the client infers from its PriceMove and PriceRange
        // records — the fields are the records' properties, named as declared.
        scalar(
                "price_move",
                composite(
                    false,
                    field("Direction", type(TypeKind.TYPE_KIND_STRING)),
                    field("Change", type(TypeKind.TYPE_KIND_FP64))))
            .setStrict(true)
            .addParameters(parameter("open_price", d))
            .addParameters(parameter("close_price", d))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        aggregate(
                "close_range",
                composite(
                    true,
                    field("Low", type(TypeKind.TYPE_KIND_FP64)),
                    field("High", type(TypeKind.TYPE_KIND_FP64))))
            .setWindow(true)
            .addParameters(parameter("x", d))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),

        // Tier 1 widened (D298): a DECIMAL and a DATE, written in the host's decimal and DateOnly.
        scalar("net_amount", decimal(15, 2, false))
            .setStrict(true)
            .addParameters(parameter("price", decimal(15, 2, true)))
            .addParameters(parameter("discount", decimal(15, 2, true)))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build(),
        scalar("week_start", type(TypeKind.TYPE_KIND_DATE))
            .setStrict(true)
            .addParameters(parameter("d", nullable(TypeKind.TYPE_KIND_DATE)))
            .setClient(chalk.ir.v1.ClientBody.getDefaultInstance())
            .build());
  }

  /** A COMPOSITE of {@code fields}, nullable as a whole or not (D291). */
  public static Type composite(boolean nullable, chalk.ir.v1.Field... fields) {
    Type.Builder type = Type.newBuilder().setKind(TypeKind.TYPE_KIND_COMPOSITE).setNullable(nullable);
    for (chalk.ir.v1.Field field : fields) {
      type.addFields(field);
    }
    return type.build();
  }

  /** The native function the M4 ADO source declares (§5), for the pushdown half of the corpus. */
  public static chalk.ir.v1.FunctionDescriptor md5() {
    return scalar("md5", nullable(TypeKind.TYPE_KIND_STRING))
        .setStrict(true)
        .addParameters(parameter("s", nullable(TypeKind.TYPE_KIND_STRING)))
        .setNative(chalk.ir.v1.NativeBody.newBuilder().setDialectName("md5"))
        .build();
  }

  private static chalk.ir.v1.FunctionDescriptor.Builder scalar(String name, Type returnType) {
    return chalk.ir.v1.FunctionDescriptor.newBuilder()
        .setName(name)
        .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR)
        .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
        .setReturnType(returnType);
  }

  private static chalk.ir.v1.FunctionDescriptor.Builder aggregate(String name, Type returnType) {
    return chalk.ir.v1.FunctionDescriptor.newBuilder()
        .setName(name)
        .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_AGGREGATE)
        .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
        .setReturnType(returnType);
  }

  private static chalk.ir.v1.FunctionDescriptor.Builder table(
      String name, chalk.ir.v1.RowType returns) {
    return chalk.ir.v1.FunctionDescriptor.newBuilder()
        .setName(name)
        .setKind(chalk.ir.v1.FunctionKind.FUNCTION_KIND_TABLE)
        .setVolatility(chalk.ir.v1.Volatility.VOLATILITY_IMMUTABLE)
        .setReturnsTable(returns);
  }

  private static chalk.ir.v1.Parameter parameter(String name, Type type) {
    return chalk.ir.v1.Parameter.newBuilder().setName(name).setType(type).build();
  }

  private static chalk.ir.v1.Parameter optional(String name, Type type, double defaultValue) {
    return chalk.ir.v1.Parameter.newBuilder()
        .setName(name)
        .setType(type)
        .setOptional(true)
        .setDefaultValue(
            chalk.ir.v1.Expr.newBuilder()
                .setType(type)
                .setLiteral(chalk.ir.v1.Literal.newBuilder().setFp64Value(defaultValue)))
        .build();
  }

  private static chalk.ir.v1.Field field(String name, Type type) {
    return chalk.ir.v1.Field.newBuilder().setName(name).setType(type).build();
  }

  /**
   * A catalog with the same TPC-H tables registered twice: once as the local {@code main} schema and
   * once as a REMOTE SQL source (M4, D82–D85). The remote copy is what the pushdown rules are tested
   * against, so the Java tests can check what a descriptor allows without a database anywhere near
   * them.
   *
   * @param name the remote schema's name, which is also its source id
   * @param capabilities what that source claims it can do
   * @param profile how it spells and evaluates SQL
   */
  public static CatalogContext withRemote(
      String name, SourceCapabilities capabilities, chalk.ir.v1.DialectProfile profile) {
    return declared().toBuilder()
        .addSchemas(
            Schema.newBuilder()
                .setSourceId(name)
                .setName(name)
                .setKind(SourceKind.SOURCE_KIND_REMOTE)
                .setDialect(profile.getDialect())
                .setCapabilities(capabilities)
                .setDialectProfile(profile)
                .addTables(lineitem())
                .addTables(customer())
                .addTables(orders())
                .addTables(nation())
                .addTables(region())
                .addTables(supplier())
                // M5: a wide fact table with a string key, for the cross-source strategies. Its
                // foreign key is cleared because `symbols` is not in this schema.
                .addTables(bars().toBuilder().clearForeignKeys().build())
                // The native function of §5 only makes sense on a source that takes queries; the
                // capability-matrix tests hand this method descriptors that take none.
                .addAllFunctions(
                    capabilities.getQueryLanguage() == QueryLanguage.QUERY_LANGUAGE_SQL
                            || capabilities.getQueryLanguage() == QueryLanguage.QUERY_LANGUAGE_IR
                        ? java.util.List.of(md5())
                        : java.util.List.<chalk.ir.v1.FunctionDescriptor>of()))
        .build();
  }


  /**
   * A catalog with two remote sources and a partitioned table (D106, M5). {@code bars_by_symbol}
   * lives in a schema of its own — as a declaration nobody serves — and its five partitions are
   * physical tables named {@code bars_<symbol>} in {@code a} and {@code b}, keyed by column 0.
   */
  public static CatalogContext withPartitions(chalk.ir.v1.DialectProfile profile) {
    SourceCapabilities capabilities = fullSqlCapabilities().build();
    String[] inA = {"BTCUSDT", "ETHUSDT", "SOLUSDT"};
    String[] inB = {"ADAUSDT", "XRPUSDT"};

    Schema.Builder a =
        Schema.newBuilder()
            .setSourceId("a")
            .setName("a")
            .setKind(SourceKind.SOURCE_KIND_REMOTE)
            .setDialect(profile.getDialect())
            .setCapabilities(capabilities)
            .setDialectProfile(profile);
    for (String symbol : inA) {
      a.addTables(partition(symbol));
    }

    Schema.Builder b =
        Schema.newBuilder()
            .setSourceId("b")
            .setName("b")
            .setKind(SourceKind.SOURCE_KIND_REMOTE)
            .setDialect(profile.getDialect())
            .setCapabilities(capabilities)
            .setDialectProfile(profile);
    for (String symbol : inB) {
      b.addTables(partition(symbol));
    }

    chalk.ir.v1.Partitioning.Builder partitioning =
        chalk.ir.v1.Partitioning.newBuilder().setPartitionColumn(0);
    for (String symbol : inA) {
      partitioning.addPartitions(partitionOf("a", symbol));
    }
    for (String symbol : inB) {
      partitioning.addPartitions(partitionOf("b", symbol));
    }

    Schema.Builder view =
        Schema.newBuilder()
            .setSourceId("federated")
            .setName("federated")
            .setKind(SourceKind.SOURCE_KIND_LOCAL)
            .addTables(
                bars().toBuilder()
                    .setName("bars_by_symbol")
                    .clearForeignKeys()
                    .clearIndexes()
                    .clearCollations()
                    .setPartitioning(partitioning)
                    .build());

    return declared().toBuilder().addSchemas(a).addSchemas(b).addSchemas(view).build();
  }

  private static Table partition(String symbol) {
    // No foreign key: `bars` names `symbols` as its parent, and a partition lives in a schema that
    // has no `symbols` in it.
    return bars().toBuilder()
        .setName("bars_" + symbol.toLowerCase(java.util.Locale.ROOT))
        .clearForeignKeys()
        .build();
  }

  private static chalk.ir.v1.Partition partitionOf(String schema, String symbol) {
    return chalk.ir.v1.Partition.newBuilder()
        .setSourceId(schema)
        .setTable("bars_" + symbol.toLowerCase(java.util.Locale.ROOT))
        .setValue(
            chalk.ir.v1.Expr.newBuilder()
                .setType(type(chalk.ir.v1.TypeKind.TYPE_KIND_STRING))
                .setLiteral(chalk.ir.v1.Literal.newBuilder().setStringValue(symbol)))
        .setRowCount(20_160)
        .build();
  }

  /** The SQLite profile the .NET presets ship, written out for the Java tests. */
  public static chalk.ir.v1.DialectProfile sqliteProfile() {
    return chalk.ir.v1.DialectProfile.newBuilder()
        .setDialect("sqlite")
        .setQuoting(chalk.ir.v1.IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
        .setQuotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
        .setUnquotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
        .setCaseSensitiveIdentifiers(false)
        .setConformance(chalk.ir.v1.SqlConformance.SQL_CONFORMANCE_LENIENT)
        .setMaxNumericPrecision(15)
        .setMaxTimestampPrecision(3)
        .setHasBoolean(false)
        .setDefaultNullCollation(chalk.ir.v1.NullCollation.NULL_COLLATION_LOW)
        .setSupportsNullOrderingClause(false)
        .setStringCollation(chalk.ir.v1.StringCollation.STRING_COLLATION_BINARY)
        .build();
  }

  /** The DuckDB profile the .NET presets ship. */
  public static chalk.ir.v1.DialectProfile duckDbProfile() {
    return chalk.ir.v1.DialectProfile.newBuilder()
        .setDialect("duckdb")
        .setQuoting(chalk.ir.v1.IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
        .setQuotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
        .setUnquotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
        .setCaseSensitiveIdentifiers(false)
        .setConformance(chalk.ir.v1.SqlConformance.SQL_CONFORMANCE_LENIENT)
        .setMaxNumericPrecision(38)
        .setMaxTimestampPrecision(6)
        .setHasBoolean(true)
        .setDefaultNullCollation(chalk.ir.v1.NullCollation.NULL_COLLATION_LAST)
        .setSupportsNullOrderingClause(true)
        .setStringCollation(chalk.ir.v1.StringCollation.STRING_COLLATION_BINARY)
        .build();
  }

  /**
   * The PostgreSQL profile the .NET presets ship. Its string collation is the locale's, not
   * Chalk's, which is what the preset says: only a {@code C} or {@code POSIX} cluster compares by
   * code point, and a host whose cluster does says so itself.
   */
  public static chalk.ir.v1.DialectProfile postgresProfile() {
    return chalk.ir.v1.DialectProfile.newBuilder()
        .setDialect("postgresql")
        .setQuoting(chalk.ir.v1.IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
        .setQuotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
        .setUnquotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_TO_LOWER)
        .setCaseSensitiveIdentifiers(true)
        .setConformance(chalk.ir.v1.SqlConformance.SQL_CONFORMANCE_LENIENT)
        .setMaxNumericPrecision(38)
        .setMaxTimestampPrecision(6)
        .setHasBoolean(true)
        .setDefaultNullCollation(chalk.ir.v1.NullCollation.NULL_COLLATION_HIGH)
        .setSupportsNullOrderingClause(true)
        .setStringCollation(chalk.ir.v1.StringCollation.STRING_COLLATION_LOCALE)
        .build();
  }

  /** Everything a fully capable SQL source declares — the starting point most tests subtract from. */
  public static SourceCapabilities.Builder fullSqlCapabilities() {
    return SourceCapabilities.newBuilder()
        .setQueryLanguage(chalk.ir.v1.QueryLanguage.QUERY_LANGUAGE_SQL)
        .addAllPushablePredicates(
            java.util.List.of(
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_EQ,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_RANGE,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_IN,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_IS_NULL,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_LIKE,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_NOT,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_AND,
                chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_OR))
        .addAllPushableFunctions(
            java.util.List.of(
                chalk.ir.v1.FunctionId.FUNCTION_ID_ADD,
                chalk.ir.v1.FunctionId.FUNCTION_ID_SUBTRACT,
                chalk.ir.v1.FunctionId.FUNCTION_ID_MULTIPLY,
                chalk.ir.v1.FunctionId.FUNCTION_ID_DIVIDE,
                chalk.ir.v1.FunctionId.FUNCTION_ID_UPPER,
                chalk.ir.v1.FunctionId.FUNCTION_ID_LOWER))
        .addAllPushableAggregates(
            java.util.List.of(
                chalk.ir.v1.AggregateFunctionId.AGGREGATE_FUNCTION_ID_COUNT,
                chalk.ir.v1.AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM,
                chalk.ir.v1.AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM0,
                chalk.ir.v1.AggregateFunctionId.AGGREGATE_FUNCTION_ID_MIN,
                chalk.ir.v1.AggregateFunctionId.AGGREGATE_FUNCTION_ID_MAX))
        .setSupportsProject(true)
        .setSupportsSort(true)
        .setSupportsLimit(true)
        .setSupportsOffset(true)
        .setSupportsDistinct(true)
        .setSupportsGroupBy(true)
        .setSupportsHaving(true)
        .setSupportsInnerJoin(true)
        .setSupportsOuterJoin(true)
        .setSupportsSemiAntiJoin(true)
        .setMaxInList(200)
        .setSupportsParameters(true);
  }

  /**
   * {@code bars_small} (D53): the same generator at 1 000 minutes per symbol, with the same declared
   * collation and the same unique {@code (symbol, ts)} index, so a window plan over it has exactly
   * the shape it has over {@code bars}. The reference executor runs the window corpus here because
   * it materialises every frame.
   */
  public static Table barsSmall() {
    return Table.newBuilder()
        .setName("bars_small")
        .setRowCount(5_000)
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("open", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("high", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("low", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("close", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("volume", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("vwap", decimal(28, 10, true)))
        .addColumns(column("trade_count", nullable(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(1).addColumns(0))
        .addCollations(
            TableCollation.newBuilder()
                .addKeys(ascending(1))
                .addKeys(ascending(0)))
        .addIndexes(index("ix_bars_small_symbol_ts", true, 0, 1))
        .addForeignKeys(foreignKey("fk_bars_small_symbols_0", 0, "symbols", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * {@code bars_clustered} (D257): {@code bars_small} again, with the (symbol, ts) index clustered
   * and its copy covering exactly what the moving average projects — symbol, ts and close. The pair
   * is the corpus's comparison: 02 takes the permutation and gathers, 02b takes the copy and streams.
   */
  public static Table barsClustered() {
    return Table.newBuilder()
        .setName("bars_clustered")
        .setRowCount(5_000)
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("open", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("high", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("low", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("close", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("volume", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("vwap", decimal(28, 10, true)))
        .addColumns(column("trade_count", nullable(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(1).addColumns(0))
        .addCollations(
            TableCollation.newBuilder()
                .addKeys(ascending(1))
                .addKeys(ascending(0)))
        .addIndexes(
            clusteredIndex(
                "ix_bars_clustered_symbol_ts", true, new int[] {0, 1}, new int[] {0, 1, 5}))
        .addForeignKeys(foreignKey("fk_bars_clustered_symbols_0", 0, "symbols", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** Every {@link TypeKind} as one column, for the type-mapping test. */
  public static CatalogContext allTypes() {
    Table.Builder table = Table.newBuilder().setName("all_types").setRowCount(1);
    table.addColumns(column("c_bool", type(TypeKind.TYPE_KIND_BOOL)));
    table.addColumns(column("c_i8", type(TypeKind.TYPE_KIND_I8)));
    table.addColumns(column("c_i16", type(TypeKind.TYPE_KIND_I16)));
    table.addColumns(column("c_i32", type(TypeKind.TYPE_KIND_I32)));
    table.addColumns(column("c_i64", type(TypeKind.TYPE_KIND_I64)));
    table.addColumns(column("c_fp32", type(TypeKind.TYPE_KIND_FP32)));
    table.addColumns(column("c_fp64", type(TypeKind.TYPE_KIND_FP64)));
    table.addColumns(column("c_string", type(TypeKind.TYPE_KIND_STRING)));
    table.addColumns(column("c_binary", type(TypeKind.TYPE_KIND_BINARY)));
    table.addColumns(column("c_date", type(TypeKind.TYPE_KIND_DATE)));
    table.addColumns(column("c_time", precise(TypeKind.TYPE_KIND_TIME, 6)));
    table.addColumns(column("c_timestamp", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)));
    table.addColumns(column("c_timestamp_tz", precise(TypeKind.TYPE_KIND_TIMESTAMP_TZ, 9)));
    table.addColumns(column("c_decimal", decimal(28, 10, false)));
    table.addColumns(column("c_uuid", type(TypeKind.TYPE_KIND_UUID)));
    table.addColumns(column("c_interval_day", type(TypeKind.TYPE_KIND_INTERVAL_DAY)));
    table.addColumns(column("c_interval_year", type(TypeKind.TYPE_KIND_INTERVAL_YEAR)));
    table.addColumns(column("c_nullable_string", nullable(TypeKind.TYPE_KIND_STRING)));
    return CatalogContext.newBuilder()
        .setContextId("types")
        .setEpoch(1)
        .addSchemas(
            Schema.newBuilder()
                .setSourceId("mem")
                .setName("main")
                .setKind(SourceKind.SOURCE_KIND_LOCAL)
                .addTables(table))
        .build();
  }

  /**
   * {@code bars}, sorted by {@code (ts, symbol)} with that collation declared, so {@code ORDER BY
   * ts} is sort-eliminated (D18, rev 3's M1 exit criterion).
   */
  public static Table bars() {
    return Table.newBuilder()
        .setName("bars")
        .setRowCount(100_800)
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("open", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("high", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("low", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("close", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("volume", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("vwap", decimal(28, 10, true)))
        .addColumns(column("trade_count", nullable(TypeKind.TYPE_KIND_I32)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(1).addColumns(0))
        .addCollations(
            TableCollation.newBuilder()
                .addKeys(ascending(1))
                .addKeys(ascending(0)))
        // M2 (D40): a unique index on (symbol, ts) — a permutation, one int per row — and one on
        // (ts, symbol), which is the declared collation and therefore costs nothing.
        .addIndexes(index("ix_bars_symbol_ts", true, 0, 1))
        .addIndexes(index("ix_bars_ts_symbol", false, 1, 0))
        .addForeignKeys(foreignKey("fk_bars_symbols_0", 0, "symbols", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** The small dimension table. M1 uses it standalone; it exists so M5 need not invent it. */
  public static Table symbols() {
    return Table.newBuilder()
        .setName("symbols")
        .setRowCount(5)
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("base", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("quote", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("tick_size", decimal(28, 10, false)))
        // Step 19's LIST column (D58): one row's array is null and one is empty.
        .addColumns(column("tags", list(nullable(TypeKind.TYPE_KIND_STRING))))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        // No indexes: five rows is not a table anything is looked up in, and leaving one table
        // without one keeps the no-index path in the corpus.
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * The graft's six adversarial rows (D164, {@code 25-coverage-graft.md} §1): two regions, a
   * duplicate {@code amount}, one NULL {@code amount}, <b>no</b> declared collation and <b>no</b>
   * unique key. Ties, nulls and partitions in six rows, and nothing to fall back on.
   */
  public static Table sales() {
    return Table.newBuilder()
        .setName("sales")
        .setRowCount(6)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("region", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("amount", nullable(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("label", type(TypeKind.TYPE_KIND_STRING)))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * Four rows ordered by {@code k} with <b>no</b> unique key (D164): {@code k} repeats at 2 and
   * skips 3, so a merge join and a merge union stay reachable over a key that is not a key.
   */
  public static Table sorted() {
    return Table.newBuilder()
        .setName("sorted")
        .setRowCount(4)
        .addColumns(column("k", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("v", type(TypeKind.TYPE_KIND_STRING)))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * The prefix corpus's table (D282): a trie the host owns, declared INDEX_KIND_PREFIX over one
   * STRING column. It claims no ordering at all, which is what the kind is for.
   */
  public static Table terms() {
    return Table.newBuilder()
        .setName("terms")
        .setRowCount(2_007)
        .addColumns(column("name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("text", type(TypeKind.TYPE_KIND_STRING)))
        .addIndexes(
            chalk.ir.v1.Index.newBuilder()
                .setName("ix_terms_name")
                .setKind(chalk.ir.v1.IndexKind.INDEX_KIND_PREFIX)
                .addColumns(0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * The composite-column table (D302): {@code bid}, {@code ask} and {@code venue} are members whose
   * types are records, so each is a COMPOSITE of the record's properties, named as declared. Keyed
   * and ordered by {@code id}, indexed by {@code symbol}, and nothing over a composite column.
   */
  public static Table quotes() {
    Type side =
        composite(
            false,
            field("Price", type(TypeKind.TYPE_KIND_FP64)),
            field("Size", type(TypeKind.TYPE_KIND_I64)));
    return Table.newBuilder()
        .setName("quotes")
        .setRowCount(240)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("bid", side))
        .addColumns(column("ask", side.toBuilder().setNullable(true).build()))
        .addColumns(
            column(
                "venue",
                composite(
                    true,
                    field("Name", type(TypeKind.TYPE_KIND_STRING)),
                    field("Country", nullable(TypeKind.TYPE_KIND_STRING)))))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .addIndexes(index("ix_quotes_symbol", false, 1))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /**
   * The irregular trade series the {@code SESSION} corpus runs on (D55): collated by
   * {@code (symbol, ts)}, which is exactly the order a session window requires, with NULL times last.
   */
  public static Table tradesSparse() {
    return Table.newBuilder()
        .setName("trades_sparse")
        .setRowCount(5 * (600 + 2))
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("ts", nullablePrecise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("price", type(TypeKind.TYPE_KIND_FP64)))
        .addColumns(column("size", type(TypeKind.TYPE_KIND_I64)))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)).addKeys(ascending(1)))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** The TPC-H-shaped table: a second domain, so nothing overfits to ordered fact tables (rev 3 §8). */
  public static Table lineitem() {
    return Table.newBuilder()
        .setName("lineitem")
        .setRowCount(60_000)
        .addColumns(column("l_orderkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("l_linenumber", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("l_partkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("l_suppkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("l_quantity", decimal(15, 2, false)))
        .addColumns(column("l_extendedprice", decimal(15, 2, false)))
        .addColumns(column("l_discount", decimal(15, 2, false)))
        .addColumns(column("l_tax", decimal(15, 2, false)))
        .addColumns(column("l_returnflag", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("l_linestatus", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("l_shipdate", type(TypeKind.TYPE_KIND_DATE)))
        .addColumns(column("l_commitdate", type(TypeKind.TYPE_KIND_DATE)))
        .addColumns(column("l_receiptdate", type(TypeKind.TYPE_KIND_DATE)))
        .addColumns(column("l_shipinstruct", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("l_shipmode", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("l_comment", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0).addColumns(1))
        .addCollations(
            TableCollation.newBuilder().addKeys(ascending(0)).addKeys(ascending(1)))
        // A non-unique index over struct rows, collation-backed; and one that needs a permutation.
        .addIndexes(index("ix_lineitem_l_orderkey", false, 0))
        .addIndexes(index("ix_lineitem_l_shipdate", false, 10))
        .addForeignKeys(foreignKey("fk_lineitem_orders_0", 0, "orders", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** The ASOF right side (D46): collated by {@code (symbol, ts)}, which is what the operator wants. */
  public static Table funding() {
    return Table.newBuilder()
        .setName("funding")
        .setRowCount(210)
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("ts", nullablePrecise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("rate", decimal(10, 8, false)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0).addColumns(1))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)).addKeys(ascending(1)))
        .addForeignKeys(foreignKey("fk_funding_symbols_0", 0, "symbols", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** Windows of time per symbol, for the range joins that have no equality to hash on. */
  public static Table events() {
    return Table.newBuilder()
        .setName("events")
        .setRowCount(20)
        .addColumns(column("id", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("symbol", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("start_ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("end_ts", precise(TypeKind.TYPE_KIND_TIMESTAMP, 9)))
        .addColumns(column("kind", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  public static Table customer() {
    return Table.newBuilder()
        .setName("customer")
        .setRowCount(1_500)
        .addColumns(column("c_custkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("c_name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("c_address", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("c_nationkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("c_phone", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("c_acctbal", decimal(15, 2, false)))
        .addColumns(column("c_mktsegment", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("c_comment", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .addIndexes(index("ix_customer_c_nationkey", false, 3))
        .addForeignKeys(foreignKey("fk_customer_nation_0", 3, "nation", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  public static Table orders() {
    return Table.newBuilder()
        .setName("orders")
        .setRowCount(15_000)
        .addColumns(column("o_orderkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("o_custkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("o_orderstatus", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("o_totalprice", decimal(15, 2, false)))
        .addColumns(column("o_orderdate", type(TypeKind.TYPE_KIND_DATE)))
        .addColumns(column("o_orderpriority", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("o_clerk", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("o_shippriority", type(TypeKind.TYPE_KIND_I32)))
        .addColumns(column("o_comment", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .addIndexes(index("ix_orders_o_custkey", false, 1))
        .addForeignKeys(foreignKey("fk_orders_customer_0", 1, "customer", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  public static Table nation() {
    return Table.newBuilder()
        .setName("nation")
        .setRowCount(25)
        .addColumns(column("n_nationkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("n_name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("n_regionkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("n_comment", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .addForeignKeys(foreignKey("fk_nation_region_0", 2, "region", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  public static Table region() {
    return Table.newBuilder()
        .setName("region")
        .setRowCount(5)
        .addColumns(column("r_regionkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("r_name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("r_comment", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  public static Table supplier() {
    return Table.newBuilder()
        .setName("supplier")
        .setRowCount(100)
        .addColumns(column("s_suppkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("s_name", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("s_address", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("s_nationkey", type(TypeKind.TYPE_KIND_I64)))
        .addColumns(column("s_phone", type(TypeKind.TYPE_KIND_STRING)))
        .addColumns(column("s_acctbal", decimal(15, 2, false)))
        .addColumns(column("s_comment", type(TypeKind.TYPE_KIND_STRING)))
        .addUniqueKeys(UniqueKey.newBuilder().addColumns(0))
        .addCollations(TableCollation.newBuilder().addKeys(ascending(0)))
        .addIndexes(index("ix_supplier_s_nationkey", false, 3))
        .addForeignKeys(foreignKey("fk_supplier_nation_0", 3, "nation", 0))
        .setRowCountKind(chalk.ir.v1.RowCountKind.ROW_COUNT_KIND_EXACT)
        .build();
  }

  /** A single-column foreign key, which is every one the corpus declares (F14). */
  private static chalk.ir.v1.ForeignKey foreignKey(
      String name, int column, String parentTable, int parentColumn) {
    return chalk.ir.v1.ForeignKey.newBuilder()
        .setName(name)
        .addColumns(column)
        .setParentTable(parentTable)
        .addParentColumns(parentColumn)
        .build();
  }

  private static Index index(String name, boolean unique, int... columns) {
    Index.Builder index =
        Index.newBuilder()
            .setName(name)
            .setKind(IndexKind.INDEX_KIND_ORDERED)
            .setUnique(unique)
            // D283: a permutation walks a contiguous slice either way, so the POCO builder declares
            // this for every index it builds.
            .setReversal(chalk.ir.v1.IndexReversal.INDEX_REVERSAL_ANY);
    for (int column : columns) {
      index.addColumns(column);
    }
    return index.build();
  }

  /**
   * A clustered index (D257) whose copy carries {@code covering} — which always includes the key
   * columns, ascending, as {@code PocoTableBuilder.Covering} builds it and the validator requires.
   */
  private static Index clusteredIndex(
      String name, boolean unique, int[] columns, int[] covering) {
    Index.Builder index =
        Index.newBuilder()
            .setName(name)
            .setKind(IndexKind.INDEX_KIND_CLUSTERED)
            .setUnique(unique)
            .setReversal(chalk.ir.v1.IndexReversal.INDEX_REVERSAL_ANY);
    for (int column : columns) {
      index.addColumns(column);
    }
    for (int column : covering) {
      index.addCovering(column);
    }
    return index.build();
  }

  public static Column column(String name, Type type) {
    return Column.newBuilder().setName(name).setType(type).build();
  }

  public static Type type(TypeKind kind) {
    return Type.newBuilder().setKind(kind).build();
  }

  public static Type nullable(TypeKind kind) {
    return Type.newBuilder().setKind(kind).setNullable(true).build();
  }

  public static Type nullablePrecise(TypeKind kind, int precision) {
    return Type.newBuilder().setKind(kind).setPrecision(precision).setNullable(true).build();
  }

  public static Type precise(TypeKind kind, int precision) {
    return Type.newBuilder().setKind(kind).setPrecision(precision).build();
  }

  /** A LIST of {@code element}, one level deep (D58). A list column is always nullable. */
  public static Type list(Type element) {
    return Type.newBuilder()
        .setKind(TypeKind.TYPE_KIND_LIST)
        .setNullable(true)
        .setElement(element)
        .build();
  }

  public static Type decimal(int precision, int scale, boolean nullable) {
    return Type.newBuilder()
        .setKind(TypeKind.TYPE_KIND_DECIMAL)
        .setPrecision(precision)
        .setScale(scale)
        .setNullable(nullable)
        .build();
  }

  private static KeyOrder ascending(int column) {
    return KeyOrder.newBuilder()
        .setColumn(column)
        .setDirection(SortDirection.SORT_DIRECTION_ASC_NULLS_LAST)
        .build();
  }
}
