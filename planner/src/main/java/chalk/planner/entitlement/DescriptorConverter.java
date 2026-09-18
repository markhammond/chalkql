package chalk.planner.entitlement;

import chalk.planner.catalog.ChalkTable;
import java.util.List;
import org.apache.calcite.config.CalciteConnectionConfig;
import org.apache.calcite.jdbc.CalciteSchema;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.prepare.CalciteCatalogReader;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelRoot;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.schema.SchemaPlus;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlOperatorTable;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.validate.SqlValidator;
import org.apache.calcite.sql.validate.SqlValidatorUtil;
import org.apache.calcite.sql2rel.SqlToRelConverter;
import org.apache.calcite.sql2rel.StandardConvertletTable;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Parses, folds, validates and converts a descriptor's SQL over one table, through the query's own
 * {@link RelOptCluster} (docs/design/16-entitlements.md §3.2, ADR 0025's second finding).
 *
 * <p>The cluster is the one thing that must be shared: {@code RexNode}s built over a second planner
 * live over another type factory, and Calcite interns row types per factory and asserts on the
 * identity, so they could not be placed in the query's tree at all. Everything else — the catalog
 * reader, the validator, the converter — is built here and is per request anyway.
 *
 * <p>One instance per request. The same one converts every entitled table, and it makes a fresh
 * validator per statement because a {@code SqlValidatorImpl} accumulates per-statement scope.
 */
public final class DescriptorConverter {
  private final RelOptCluster cluster;
  private final CalciteCatalogReader catalogReader;
  private final SqlOperatorTable operatorTable;
  private final SqlValidator.Config validatorConfig;
  private final SqlParser.Config parserConfig;
  private final SqlToRelConverter.Config converterConfig;
  private final @Nullable BoundContext context;

  /**
   * A converter for the <b>registration</b> check, which has no context at all: every
   * {@code @ctx.<name>} is accepted and becomes a placeholder typed by where it stands (§1, F42).
   */
  public static DescriptorConverter forRegistration(
      RelOptCluster cluster,
      SchemaPlus defaultSchema,
      SqlOperatorTable operatorTable,
      SqlValidator.Config validatorConfig,
      SqlParser.Config parserConfig,
      SqlToRelConverter.Config converterConfig,
      CalciteConnectionConfig connectionConfig) {
    return new DescriptorConverter(
        cluster,
        defaultSchema,
        operatorTable,
        validatorConfig,
        parserConfig,
        converterConfig,
        connectionConfig,
        null);
  }

  public DescriptorConverter(
      RelOptCluster cluster,
      SchemaPlus defaultSchema,
      SqlOperatorTable operatorTable,
      SqlValidator.Config validatorConfig,
      SqlParser.Config parserConfig,
      SqlToRelConverter.Config converterConfig,
      CalciteConnectionConfig connectionConfig,
      @Nullable BoundContext context) {
    this.cluster = cluster;
    this.operatorTable = operatorTable;
    this.validatorConfig = validatorConfig;
    this.parserConfig = parserConfig;
    // Trimming is off for this conversion and only for it. `SqlToRelConverter.convertQuery` trims
    // unused fields when its config says to, which would prune the scan under the projection — and
    // then every RexInputRef would index a *pruned* row rather than the table's, which is the one
    // property this whole class exists to hold.
    this.converterConfig = converterConfig.withTrimUnusedFields(false);
    this.context = context;
    RelDataTypeFactory typeFactory = cluster.getTypeFactory();
    this.catalogReader =
        new CalciteCatalogReader(
            CalciteSchema.from(rootOf(defaultSchema)),
            CalciteSchema.from(defaultSchema).path(null),
            typeFactory,
            connectionConfig);
  }

  private static void appendQualified(StringBuilder sql, List<String> qualified) {
    for (int i = 0; i < qualified.size(); i++) {
      sql.append(i == 0 ? "" : ".").append('"').append(qualified.get(i)).append('"');
    }
  }

  /** The type factory these expressions are built over — the query's own cluster's. */
  public RelDataTypeFactory typeFactory() {
    return cluster.getTypeFactory();
  }

  /**
   * The table {@code qualified} names, resolved through the <b>declared</b> catalog this converter
   * reads: the row the source stores, not the row a statement sees (F58, F68).
   *
   * <p>Every {@code RexInputRef} this class yields is typed by that row — a mask is evaluated over
   * the leaf's raw row and the scan beneath a leaf reads exactly that row — so anything the pass
   * builds those expressions <em>over</em> has to be scanned at the same row type, or the two halves
   * of one filter disagree about a column's nullability. A declared path's chain is the case that
   * found it: it scans the bridge and the endpoint raw (D265 §2), and a chain table with a
   * rule-protected column was being resolved through the disclosed tree, where that column is
   * widened to nullable per catalog (D161, ADR 0038).
   *
   * @return null when this catalog does not hold the table, which registration has already refused
   */
  public @Nullable RelOptTable declaredTable(List<String> qualified) {
    return catalogReader.getTableForMember(qualified);
  }

  private static SchemaPlus rootOf(SchemaPlus schema) {
    SchemaPlus root = schema;
    while (root.getParentSchema() != null) {
      root = root.getParentSchema();
    }
    return root;
  }

  /**
   * Converts {@code exprs} as the select list of {@code SELECT … FROM <table>}, and answers the
   * projected expressions in order. Every {@code RexInputRef} in them indexes the table's row type.
   */
  public List<RexNode> convert(List<String> exprs, RelOptTable relOptTable, ChalkTable table) {
    return convert(
        exprs,
        relOptTable.getQualifiedName(),
        table.schemaName() + "." + table.tableName());
  }

  /**
   * The same over a table named rather than resolved, for the registration check, which has a
   * catalog and no query.
   *
   * @param qualified the table, as the generated statement's {@code FROM} names it
   * @param where the table, as a message names it
   */
  public List<RexNode> convert(List<String> exprs, List<String> qualified, String where) {
    return convert(exprs, qualified, List.of(), where);
  }

  /**
   * The same with the table's declared parents beside it in the {@code FROM} (§3.13, D225).
   *
   * <p>A child entitled <em>through</em> a parent writes the role half of its rule conditions over
   * {@code <parent_table>.<column>}, so the parents have to be in scope for those names to resolve
   * at all. The generated statement is {@code SELECT … FROM <child>, <parent…>}, whose conversion is
   * a projection over a left-deep chain of cross joins, so every {@code RexInputRef} below
   * {@code child.width} indexes the child's row and the rest index the parents' in the order given.
   * Splitting them apart again is {@link VerdictColumns}'s job.
   *
   * @param parents the distinct parent tables, in the order their column blocks follow the child's
   */
  public List<RexNode> convert(
      List<String> exprs, List<String> qualified, List<List<String>> parents, String where) {
    StringBuilder sql = new StringBuilder("SELECT ");
    for (int i = 0; i < exprs.size(); i++) {
      sql.append(i == 0 ? "" : ", ").append(exprs.get(i)).append(" AS \"e").append(i).append('"');
    }
    sql.append(" FROM ");
    appendQualified(sql, qualified);
    for (List<String> parent : parents) {
      sql.append(", ");
      appendQualified(sql, parent);
    }

    String text = rewriteContext(sql.toString(), where);

    SqlNode parsed;
    try {
      parsed = SqlParser.create(text, parserConfig).parseQuery();
    } catch (org.apache.calcite.sql.parser.SqlParseException e) {
      throw new PolicyException(
          "the entitlement on "
              + where
              + " does not parse: "
              + e.getMessage()
              + ". Its row predicate, rule conditions, masks and placeholders are SQL in Chalk's own"
              + " dialect over the table's columns and @ctx names"
              + " (docs/design/16-entitlements.md §1).");
    }

    ContextFold.Folded folded =
        context == null
            ? new ContextFold.Folded(RegistrationFold.fold(parsed), List.of())
            : ContextFold.fold(parsed, context, parserConfig, cluster.getTypeFactory());
    SqlValidator validator =
        SqlValidatorUtil.newValidator(
            operatorTable, catalogReader, cluster.getTypeFactory(), validatorConfig);

    SqlNode validated;
    try {
      validated = validator.validate(folded.node());
    } catch (RuntimeException e) {
      throw new PolicyException(
          "the entitlement on "
              + where
              + " does not type-check against the table: "
              + rootMessage(e)
              + " (docs/design/16-entitlements.md §1).");
    }

    SqlToRelConverter converter =
        new SqlToRelConverter(
            null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE,
            converterConfig);
    RelRoot root = converter.convertQuery(validated, false, true);

    Project project = projectOf(root.rel);
    if (project == null) {
      throw new PolicyException(
          "the entitlement on "
              + where
              + " converts to a shape the pass cannot place on a leaf. Its expressions must be"
              + " scalar over the table's own row; a condition over another catalog table is not"
              + " supported (docs/design/16-entitlements.md §10).");
    }
    List<RexNode> converted = bind(project.getProjects(), folded.boundScalars());
    for (RexNode expression : converted) {
      refuseCatalogSubQuery(expression, where);
    }
    return converted;
  }

  /**
   * Refuses a descriptor expression that reads another <em>catalog</em> table (§3.13, D225).
   *
   * <p>§2's vocabulary is this table's columns, context scalars, context lists and context
   * relations, and nothing else; Chalk converts with {@code expand = false}, so a sub-query survives
   * as a {@code RexSubQuery} in the projection rather than turning the shape into something
   * {@link #projectOf} would have declined. A sub-query over a context relation is legitimate and
   * becomes a semi-join; one over a catalog table is a join the pass never asked for, whose rows it
   * could neither sanitise nor cost, and whose answer is {@code through}: an explicit relationship
   * is the only way visibility derives from another table (owner 2026-09-11).
   */
  private static void refuseCatalogSubQuery(RexNode expression, String where) {
    expression.accept(
        new org.apache.calcite.rex.RexVisitorImpl<Void>(true) {
          @Override
          public Void visitSubQuery(org.apache.calcite.rex.RexSubQuery subQuery) {
            String table = catalogTableIn(subQuery.rel);
            if (table != null) {
              throw new PolicyException(
                  "the entitlement on "
                      + where
                      + " reads the catalog table "
                      + table
                      + ". A row predicate, a rule condition, a mask and a placeholder are scalar"
                      + " over this table's own row and the context — this table's columns,"
                      + " @ctx scalars, @ctx lists and @ctx relations — and never another catalog"
                      + " table. Visibility that derives from another table is declared as a"
                      + " `through` parent, which the pass compiles into one join against that"
                      + " table's own entitled scan (docs/design/16-entitlements.md §3.13, D225).");
            }
            return super.visitSubQuery(subQuery);
          }
        });
  }

  /** The first catalog table a relation scans, or null when it scans only context relations. */
  private static @Nullable String catalogTableIn(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return scan.getTable().unwrap(ContextTable.class) == null
          ? String.join(".", scan.getTable().getQualifiedName())
          : null;
    }
    for (RelNode input : rel.getInputs()) {
      String found = catalogTableIn(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  /**
   * Every placeholder a shape-only fold wrote, as the {@link BoundParam} it stands for (D209).
   *
   * <p>The fold numbers its placeholders per statement, because that is what the parser and the
   * converter count; the plan numbers them per request, because a name bound once is one parameter
   * whichever table's descriptor reads it. This is where the one becomes the other.
   */
  private List<RexNode> bind(List<RexNode> converted, List<String> boundScalars) {
    if (boundScalars.isEmpty() || context == null) {
      return converted;
    }
    org.apache.calcite.rex.RexShuttle shuttle =
        new org.apache.calcite.rex.RexShuttle() {
          @Override
          public RexNode visitDynamicParam(org.apache.calcite.rex.RexDynamicParam param) {
            if (param instanceof BoundParam || param.getIndex() >= boundScalars.size()) {
              return param;
            }
            String name = boundScalars.get(param.getIndex());
            // The host's declared type, not the one the validator inferred for the placeholder: the
            // shape is what the host promised to bind, and a cast the validator widened is not it.
            return new BoundParam(
                new chalk.planner.types.TypeMapper(cluster.getTypeFactory())
                    .toCalcite(context.scalar(name).getType()),
                context.scalarSlot(name),
                name);
          }
        };
    List<RexNode> bound = new java.util.ArrayList<>(converted.size());
    for (RexNode expression : converted) {
      bound.add(expression.accept(shuttle));
    }
    return bound;
  }

  /** {@code @ctx.<name>} → the bound form, with an unbound name named rather than "column not found". */
  private String rewriteContext(String sql, String where) {
    try {
      return context == null ? ContextSql.rewriteForRegistration(sql) : ContextSql.rewrite(sql, context);
    } catch (IllegalArgumentException e) {
      throw new PolicyException("the entitlement on " + where + ": " + e.getMessage());
    }
  }

  private static String rootMessage(Throwable error) {
    Throwable root = error;
    while (root.getCause() != null && root.getCause() != root) {
      root = root.getCause();
    }
    String message = root.getMessage();
    return message == null ? root.toString() : message;
  }

  /**
   * The single projection over the table scan — or, with parents in the {@code FROM}, over the
   * left-deep chain of cross joins of them — the generated statement must convert to.
   *
   * <p>Anything else means the expressions were not scalar over the row: a sub-query over another
   * catalog table converts to a shape with a relation in it, and §3.13 answers that with
   * {@code through} rather than with a join the pass never asked for.
   */
  private static Project projectOf(RelNode rel) {
    if (!(rel instanceof Project project)) {
      return null;
    }
    return isScanTree(project.getInput()) ? project : null;
  }

  /** A table scan, or a cross join of them, which is what {@code FROM a, b, c} converts to. */
  private static boolean isScanTree(RelNode rel) {
    if (rel instanceof TableScan) {
      return true;
    }
    if (rel instanceof org.apache.calcite.rel.core.Join join) {
      return isScanTree(join.getLeft()) && isScanTree(join.getRight());
    }
    return false;
  }
}
