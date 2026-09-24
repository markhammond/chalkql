// Derived from Apache Calcite's org.apache.calcite.prepare.PlannerImpl (Apache License 2.0,
// Copyright 2012-2026 The Apache Software Foundation), and identical to it but for the one step it
// omits: the structured-type flattener. See NOTICE.
package chalk.planner.plan;

import static java.util.Objects.requireNonNull;

import com.google.common.collect.ImmutableList;
import java.io.Reader;
import java.util.List;
import org.apache.calcite.adapter.java.JavaTypeFactory;
import org.apache.calcite.config.CalciteConnectionConfig;
import org.apache.calcite.config.CalciteConnectionConfigImpl;
import org.apache.calcite.config.CalciteConnectionProperty;
import org.apache.calcite.config.CalciteSystemProperty;
import org.apache.calcite.jdbc.CalciteSchema;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.plan.Context;
import org.apache.calcite.plan.ConventionTraitDef;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptCostFactory;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.RelTraitDef;
import org.apache.calcite.plan.RelTraitSet;
import org.apache.calcite.plan.volcano.VolcanoPlanner;
import org.apache.calcite.prepare.CalciteCatalogReader;
import org.apache.calcite.prepare.CalciteSqlValidator;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelRoot;
import org.apache.calcite.rel.metadata.CachingRelMetadataProvider;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeSystem;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexExecutor;
import org.apache.calcite.runtime.Hook;
import org.apache.calcite.schema.SchemaPlus;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlOperatorTable;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.util.SqlOperatorTables;
import org.apache.calcite.sql.validate.SqlValidator;
import org.apache.calcite.sql2rel.RelDecorrelator;
import org.apache.calcite.sql2rel.SqlRexConvertletTable;
import org.apache.calcite.sql2rel.SqlToRelConverter;
import org.apache.calcite.sql2rel.TopDownGeneralDecorrelator;
import org.apache.calcite.tools.FrameworkConfig;
import org.apache.calcite.tools.Planner;
import org.apache.calcite.tools.Program;
import org.apache.calcite.tools.RelBuilder;
import org.apache.calcite.tools.ValidationException;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Chalk's planner driver (D292, ADR 0077): parse, validate and convert exactly as Calcite's own
 * {@code PlannerImpl} does, and never run the structured-type flattener.
 *
 * <p>{@code PlannerImpl} converts with {@code convertQuery}, then calls {@code flattenTypes} on the
 * result, then decorrelates. Flattened, a struct-valued call is split into one call per field —
 * {@code ROW(f(x).category, f(x).confidence)} — and a struct-valued aggregate into a two-column
 * {@code Aggregate} whose references are then out of range. Unflattened, every tree is what the IR
 * wants: one call, one struct column, field accesses over it (design 51 §0). The flattener did
 * nothing for Chalk before structs existed, so nothing is lost by not running it.
 *
 * <p>Why a class and not a line: every member the flattening step touches is private to {@code
 * PlannerImpl} — its validator, catalog reader, cluster planner and state — and {@code transform}
 * refuses to run until {@code rel()} has moved the state on, so neither a subclass nor a converter of
 * Chalk's own beside it can skip the step. So this is {@code PlannerImpl}, member for member: the same
 * state machine, the same type factory and Volcano planner with Calcite's default rules registered,
 * the same catalog reader and validator, the same choice of decorrelator from the connection config,
 * the same {@code transform}. What differs is that {@link #rel} and {@link #expandView} — the two
 * places {@code PlannerImpl} flattens — do not.
 *
 * <p>{@link #expandView} is where a SQL-bodied table function's expansion is converted, from inside
 * the statement's own conversion. Flattening that subtree and not the tree it is spliced into would
 * leave the driver half-flattened, and would keep alive the one reason {@code ChalkPartitionedScan}
 * implemented Calcite's self-flattening interface: an expansion that reads a partitioned table.
 *
 * <p>One member is Chalk's own and adds nothing to the conversion: {@link #validator()}, which hands
 * the pipeline the validator of the statement just validated, so that a check which needs an
 * expression's validated type ({@code StructSupport}) can ask before anything is converted or folded.
 */
public final class ChalkPlanner implements Planner, RelOptTable.ViewExpander {
  private final SqlOperatorTable operatorTable;
  private final ImmutableList<Program> programs;
  private final @Nullable RelOptCostFactory costFactory;
  private final Context context;
  private final CalciteConnectionConfig connectionConfig;
  private final RelDataTypeSystem typeSystem;

  /** Null means "use the default trait definitions". Raw, as {@code FrameworkConfig} hands it over. */
  @SuppressWarnings("rawtypes")
  private final @Nullable ImmutableList<RelTraitDef> traitDefs;

  private final SqlParser.Config parserConfig;
  private final SqlValidator.Config sqlValidatorConfig;
  private final SqlToRelConverter.Config sqlToRelConverterConfig;
  private final SqlRexConvertletTable convertletTable;

  private State state;

  // set in STATE_1_RESET
  @SuppressWarnings("unused")
  private boolean open;

  // set in STATE_2_READY
  private final @Nullable SchemaPlus defaultSchema;
  private @Nullable JavaTypeFactory typeFactory;
  private @Nullable RelOptPlanner planner;
  private final @Nullable RexExecutor executor;

  // set in STATE_4_VALIDATE
  private @Nullable SqlValidator validator;
  private @Nullable SqlNode validatedSqlNode;

  /** Creates a driver over a framework config, as {@code Frameworks.getPlanner} creates Calcite's. */
  public ChalkPlanner(FrameworkConfig config) {
    this.costFactory = config.getCostFactory();
    this.defaultSchema = config.getDefaultSchema();
    this.operatorTable = config.getOperatorTable();
    this.programs = config.getPrograms();
    this.parserConfig = config.getParserConfig();
    this.sqlValidatorConfig = config.getSqlValidatorConfig();
    this.sqlToRelConverterConfig = config.getSqlToRelConverterConfig();
    this.state = State.STATE_0_CLOSED;
    this.traitDefs = config.getTraitDefs();
    this.convertletTable = config.getConvertletTable();
    this.executor = config.getExecutor();
    this.context = config.getContext();
    this.connectionConfig = connConfig(context, parserConfig);
    this.typeSystem = config.getTypeSystem();
    reset();
  }

  /**
   * Gets a user-defined config and appends default connection values, exactly as {@code
   * PlannerImpl} does.
   */
  private static CalciteConnectionConfig connConfig(
      Context context, SqlParser.Config parserConfig) {
    CalciteConnectionConfigImpl config =
        context
            .maybeUnwrap(CalciteConnectionConfigImpl.class)
            .orElse(CalciteConnectionConfig.DEFAULT);
    if (!config.isSet(CalciteConnectionProperty.CASE_SENSITIVE)) {
      config =
          config.set(
              CalciteConnectionProperty.CASE_SENSITIVE,
              String.valueOf(parserConfig.caseSensitive()));
    }
    if (!config.isSet(CalciteConnectionProperty.CONFORMANCE)) {
      config =
          config.set(
              CalciteConnectionProperty.CONFORMANCE, String.valueOf(parserConfig.conformance()));
    }
    return config;
  }

  /** Makes sure that the state is at least the given state. */
  private void ensure(State state) {
    if (state == this.state) {
      return;
    }
    if (state.ordinal() < this.state.ordinal()) {
      throw new IllegalArgumentException("cannot move to " + state + " from " + this.state);
    }
    state.from(this);
  }

  @Override
  public RelTraitSet getEmptyTraitSet() {
    return requireNonNull(planner, "planner").emptyTraitSet();
  }

  @Override
  public void close() {
    open = false;
    typeFactory = null;
    state = State.STATE_0_CLOSED;
  }

  @Override
  public void reset() {
    ensure(State.STATE_0_CLOSED);
    open = true;
    state = State.STATE_1_RESET;
  }

  @SuppressWarnings("rawtypes")
  private void ready() {
    switch (state) {
      case STATE_0_CLOSED:
        reset();
        break;
      default:
        break;
    }
    ensure(State.STATE_1_RESET);

    typeFactory = new JavaTypeFactoryImpl(typeSystem);
    RelOptPlanner planner = this.planner = new VolcanoPlanner(costFactory, context);
    RelOptUtil.registerDefaultRules(
        planner, connectionConfig.materializationsEnabled(), Hook.ENABLE_BINDABLE.get(false));
    planner.setExecutor(executor);

    state = State.STATE_2_READY;

    // If the caller specified its own trait definitions, register those instead of the defaults.
    if (this.traitDefs == null) {
      planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
      if (CalciteSystemProperty.ENABLE_COLLATION_TRAIT.value()) {
        planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);
      }
    } else {
      for (RelTraitDef def : this.traitDefs) {
        planner.addRelTraitDef(def);
      }
    }
  }

  @Override
  public SqlNode parse(final Reader reader) throws SqlParseException {
    switch (state) {
      case STATE_0_CLOSED:
      case STATE_1_RESET:
        ready();
        break;
      default:
        break;
    }
    ensure(State.STATE_2_READY);
    SqlParser parser = SqlParser.create(reader, parserConfig);
    SqlNode sqlNode = parser.parseStmt();
    state = State.STATE_3_PARSED;
    return sqlNode;
  }

  @Override
  public SqlNode validate(SqlNode sqlNode) throws ValidationException {
    ensure(State.STATE_3_PARSED);
    this.validator = createSqlValidator(createCatalogReader());
    try {
      validatedSqlNode = validator.validate(sqlNode);
    } catch (RuntimeException e) {
      throw new ValidationException(e);
    }
    state = State.STATE_4_VALIDATED;
    return validatedSqlNode;
  }

  /**
   * The validator the last {@link #validate} used, and with it the type of every node it reached: the
   * whole statement's when validation succeeded, what it had derived before it stopped when it did
   * not. Null before the first call. Not a {@code PlannerImpl} member: the pipeline reads it between
   * validation and conversion.
   */
  public @Nullable SqlValidator validator() {
    return validator;
  }

  @Override
  public Pair<SqlNode, RelDataType> validateAndGetType(SqlNode sqlNode)
      throws ValidationException {
    final SqlNode validatedNode = this.validate(sqlNode);
    final RelDataType type =
        requireNonNull(this.validator, "validator").getValidatedNodeType(validatedNode);
    return Pair.of(validatedNode, type);
  }

  @Override
  public RelDataType getParameterRowType() {
    if (state.ordinal() < State.STATE_4_VALIDATED.ordinal()) {
      throw new RuntimeException("Need to call #validate() first");
    }

    return requireNonNull(validator, "validator")
        .getParameterRowType(requireNonNull(validatedSqlNode, "validatedSqlNode"));
  }

  @SuppressWarnings("deprecation")
  @Override
  public final RelNode convert(SqlNode sql) {
    return rel(sql).rel;
  }

  /**
   * {@code PlannerImpl.rel} without {@code flattenTypes}: convert, then decorrelate with the
   * decorrelator the connection config names — Chalk turns on the general one (V26).
   */
  @Override
  public RelRoot rel(SqlNode sql) {
    ensure(State.STATE_4_VALIDATED);
    SqlNode validatedSqlNode =
        requireNonNull(
            this.validatedSqlNode, "validatedSqlNode is null. Need to call #validate() first");
    final RexBuilder rexBuilder = createRexBuilder();
    final RelOptCluster cluster =
        RelOptCluster.create(requireNonNull(planner, "planner"), rexBuilder);
    final SqlToRelConverter.Config config =
        sqlToRelConverterConfig
            .withTrimUnusedFields(false)
            .withTopDownGeneralDecorrelationEnabled(
                connectionConfig.topDownGeneralDecorrelationEnabled());
    final SqlToRelConverter sqlToRelConverter =
        new SqlToRelConverter(
            this, validator, createCatalogReader(), cluster, convertletTable, config);
    RelRoot root = sqlToRelConverter.convertQuery(validatedSqlNode, false, true);
    // PlannerImpl flattens here. Chalk does not: a struct stays one value (D292).
    final RelBuilder relBuilder = config.getRelBuilderFactory().create(cluster, null);
    if (config.isTopDownGeneralDecorrelationEnabled()) {
      root = root.withRel(TopDownGeneralDecorrelator.decorrelateQuery(root.rel, relBuilder));
    } else {
      root = root.withRel(RelDecorrelator.decorrelateQuery(root.rel, relBuilder));
    }
    state = State.STATE_5_CONVERTED;
    return root;
  }

  /**
   * {@code PlannerImpl.expandView} without {@code flattenTypes}: a SQL-bodied table function's
   * expansion, converted as the statement around it is (D292).
   */
  @Override
  public RelRoot expandView(
      RelDataType rowType,
      String queryString,
      List<String> schemaPath,
      @Nullable List<String> viewPath) {
    RelOptPlanner planner = this.planner;
    if (planner == null) {
      ready();
      planner = requireNonNull(this.planner, "planner");
    }
    SqlParser parser = SqlParser.create(queryString, parserConfig);
    SqlNode sqlNode;
    try {
      sqlNode = parser.parseQuery();
    } catch (SqlParseException e) {
      throw new RuntimeException("parse failed", e);
    }

    final CalciteCatalogReader catalogReader =
        createCatalogReader().withSchemaPath(schemaPath);
    final SqlValidator validator = createSqlValidator(catalogReader);

    final RexBuilder rexBuilder = createRexBuilder();
    final RelOptCluster cluster = RelOptCluster.create(planner, rexBuilder);
    final SqlToRelConverter.Config config =
        sqlToRelConverterConfig
            .withTrimUnusedFields(false)
            .withTopDownGeneralDecorrelationEnabled(
                connectionConfig.topDownGeneralDecorrelationEnabled());
    final SqlToRelConverter sqlToRelConverter =
        new SqlToRelConverter(this, validator, catalogReader, cluster, convertletTable, config);

    RelRoot root = sqlToRelConverter.convertQuery(sqlNode, true, false);
    // PlannerImpl flattens here too; see the class comment.
    final RelBuilder relBuilder = config.getRelBuilderFactory().create(cluster, null);
    if (config.isTopDownGeneralDecorrelationEnabled()) {
      return root.withRel(TopDownGeneralDecorrelator.decorrelateQuery(root.rel, relBuilder));
    }
    return root.withRel(RelDecorrelator.decorrelateQuery(root.rel, relBuilder));
  }

  // CalciteCatalogReader is stateless; no need to store one
  private CalciteCatalogReader createCatalogReader() {
    SchemaPlus defaultSchema = requireNonNull(this.defaultSchema, "defaultSchema");
    final SchemaPlus rootSchema = rootSchema(defaultSchema);

    return new CalciteCatalogReader(
        CalciteSchema.from(rootSchema),
        CalciteSchema.from(defaultSchema).path(null),
        getTypeFactory(),
        connectionConfig);
  }

  private SqlValidator createSqlValidator(CalciteCatalogReader catalogReader) {
    final SqlOperatorTable opTab = SqlOperatorTables.chain(operatorTable, catalogReader);
    return new CalciteSqlValidator(
        opTab,
        catalogReader,
        getTypeFactory(),
        sqlValidatorConfig
            .withDefaultNullCollation(connectionConfig.defaultNullCollation())
            .withLenientOperatorLookup(connectionConfig.lenientOperatorLookup())
            .withConformance(connectionConfig.conformance())
            .withIdentifierExpansion(true));
  }

  private static SchemaPlus rootSchema(SchemaPlus schema) {
    for (; ; ) {
      SchemaPlus parentSchema = schema.getParentSchema();
      if (parentSchema == null) {
        return schema;
      }
      schema = parentSchema;
    }
  }

  // RexBuilder is stateless; no need to store one
  private RexBuilder createRexBuilder() {
    return new RexBuilder(getTypeFactory());
  }

  @Override
  public JavaTypeFactory getTypeFactory() {
    return requireNonNull(typeFactory, "typeFactory");
  }

  @SuppressWarnings("deprecation")
  @Override
  public RelNode transform(int ruleSetIndex, RelTraitSet requiredOutputTraits, RelNode rel) {
    ensure(State.STATE_5_CONVERTED);
    rel.getCluster()
        .setMetadataProvider(
            new CachingRelMetadataProvider(
                requireNonNull(rel.getCluster().getMetadataProvider(), "metadataProvider"),
                rel.getCluster().getPlanner()));
    Program program = programs.get(ruleSetIndex);
    return program.run(
        requireNonNull(planner, "planner"),
        rel,
        requiredOutputTraits,
        ImmutableList.of(),
        ImmutableList.of());
  }

  /** Stage of a statement in the query-preparation lifecycle, as {@code PlannerImpl} has them. */
  private enum State {
    STATE_0_CLOSED {
      @Override
      void from(ChalkPlanner planner) {
        planner.close();
      }
    },
    STATE_1_RESET {
      @Override
      void from(ChalkPlanner planner) {
        planner.ensure(STATE_0_CLOSED);
        planner.reset();
      }
    },
    STATE_2_READY {
      @Override
      void from(ChalkPlanner planner) {
        STATE_1_RESET.from(planner);
        planner.ready();
      }
    },
    STATE_3_PARSED,
    STATE_4_VALIDATED,
    STATE_5_CONVERTED;

    /** Moves the planner from its current state to this one. */
    void from(ChalkPlanner planner) {
      throw new IllegalArgumentException("cannot move from " + planner.state + " to " + this);
    }
  }
}
