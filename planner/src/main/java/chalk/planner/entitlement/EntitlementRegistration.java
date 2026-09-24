package chalk.planner.entitlement;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.UserFunctions;
import chalk.planner.plan.ChalkOperatorTable;
import chalk.planner.plan.SqlConfigs;
import chalk.ir.v1.Disclosure;
import chalk.planner.types.ChalkTypeSystem;
import java.util.BitSet;
import java.util.List;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.hep.HepPlanner;
import org.apache.calcite.plan.hep.HepProgram;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexSimplify;
import org.apache.calcite.schema.SchemaPlus;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Type-checks every entitlement's SQL when the catalog is registered
 * (docs/design/16-entitlements.md §1, §3.2; F42).
 *
 * <p>§1 says the row predicate is boolean, every rule condition is boolean, and every mask and
 * placeholder has the column's type, and that the check runs once at registration through a
 * throwaway cluster. This is that check, and it uses the same {@link DescriptorConverter} the pass
 * uses per request, so what registration accepts is exactly what the pass will later convert.
 *
 * <p>Two things are worth knowing about how it types.
 *
 * <p><b>The context is not here.</b> A {@code RequestContext} is bound per request and the same
 * descriptor serves every principal, so registration cannot know what {@code @ctx.mask_key} is. It
 * therefore accepts any {@code @ctx} name and checks the expression's type over the rest of it
 * ({@link RegistrationFold}): a name in a value position becomes a value of type {@code ANY}, which
 * type-checks wherever it stands and gives the expression around it nothing, and a membership over a
 * list is checked as an equality against one such value per key.
 * An expression that is <em>nothing but</em> a context reference has no rest to check and is
 * skipped, which is the one thing this cannot say anything about.
 *
 * <p><b>A mask reads its own column and nothing else protected (D220).</b> The sanitiser is
 * evaluated over the leaf's <em>raw</em> row, which is what makes masking possible at all, so a mask
 * that read another protected column would embed that column's raw value in this one's disclosed
 * value — and a principal entitled to neither would be handed one. Every mask is therefore checked
 * against the columns the descriptor protects, and a reference to another of them is refused naming
 * both. A rule <em>condition</em> is a different matter and may read anything: what it discloses is
 * one bit, and that is the policy author's own choice (§1).
 *
 * <p><b>The comparison is before coercion.</b> Calcite's implicit coercion accepts a character
 * literal where a DATE is wanted, which is exactly what let {@code '****'} through as the mask of a
 * DATE column and made it a run-time cast failure instead of a refusal here. So the expression is
 * converted in a position with <em>no</em> target type — a select item — and its own derived type is
 * then compared with the column's declared type by kind, with numbers assignable to numbers and
 * characters to characters and nothing else. A coercion Calcite would have applied never happens.
 */
public final class EntitlementRegistration {
  private EntitlementRegistration() {}

  /**
   * Checks every entitled table of {@code descriptor}, and answers which of their columns are
   * nullable in the row type a statement is written over (F58, {@link DisclosedNullability}).
   *
   * <p>The two belong together because they are one reading of the same SQL: the check converts
   * every mask and every placeholder to compare its type with the column's, and that converted
   * expression's own <em>nullability</em> is the whole of what the row type needs.
   *
   * @param defaultSchema the registered catalog's schemas <b>as declared</b>, so a mask is
   *     type-checked over the row the source stores rather than over the row a statement sees
   * @throws InvalidCatalogException naming the table, the column, the field and the SQL text
   */
  public static DisclosedNullability check(
      CatalogContext descriptor, SchemaPlus defaultSchema, UserFunctions functions) {
    RelDataTypeFactory typeFactory = new JavaTypeFactoryImpl(ChalkTypeSystem.INSTANCE);
    RelOptCluster cluster =
        RelOptCluster.create(
            new HepPlanner(HepProgram.builder().build()), new RexBuilder(typeFactory));
    DescriptorConverter converter =
        DescriptorConverter.forRegistration(
            cluster,
            defaultSchema,
            ChalkOperatorTable.instance(List.of(), functions),
            SqlConfigs.validator(SqlConfigs.DEFAULT_CONFORMANCE),
            SqlConfigs.parser(SqlConfigs.DEFAULT_CONFORMANCE),
            SqlConfigs.sqlToRel(),
            SqlConfigs.connectionConfig());

    DisclosedNullability.Builder nullability = new DisclosedNullability.Builder();
    for (int s = 0; s < descriptor.getSchemasCount(); s++) {
      Schema schema = descriptor.getSchemas(s);
      for (int t = 0; t < schema.getTablesCount(); t++) {
        Table table = schema.getTables(t);
        if (!table.hasEntitlement()) {
          continue;
        }
        String path =
            "schemas[" + s + "] (" + schema.getName() + ").tables[" + t + "] ("
                + table.getName() + ").entitlement";
        checkTable(converter, typeFactory, descriptor, schema, table, path, nullability, functions);
      }
    }
    return nullability.build();
  }

  private static void checkTable(
      DescriptorConverter converter,
      RelDataTypeFactory typeFactory,
      CatalogContext catalog,
      Schema schema,
      Table table,
      String path,
      DisclosedNullability.Builder nullability,
      UserFunctions functions) {
    List<String> qualified = List.of(schema.getName(), table.getName());
    String where = schema.getName() + "." + table.getName();
    RelDataType rowType = new ChalkTable(table, schema).getRowType(typeFactory);
    TableEntitlement entitlement = table.getEntitlement();

    // Visibility derived through a parent (§3.13, D225): the shape of the relationship first, then
    // the parents' rows beside the child's in the FROM, so that `<parent_table>.<column>` resolves
    // in a rule condition at all.
    ThroughParents parents = ThroughParents.resolve(catalog, schema, table, typeFactory, path);
    List<List<String>> from = parents.qualifiedNames();

    // The row predicate is converted with the parents *out* of scope, which is what refuses a
    // reference to one: it is over this table's own row and the context, and the parent's rows are
    // reached by the `through` entry itself (§3.13).
    checkOwnRow(converter, typeFactory, qualified, parents, where, path + ".row_predicate",
        "the row predicate", entitlement.getRowPredicate(), null,
        "A row predicate is over this table's own row and the context; the parent's rows are "
            + "reached by the `through` entry itself, whose join the pass ORs in");

    // A path's endpoint predicate is over the *endpoint's* own row and the context (D265 §2): it is
    // where the kind is held, and the pass folds it at the far end of the chain. Converting it with
    // the target out of scope is what refuses a reference to the target's columns, which the pass
    // could not evaluate there.
    for (int i = 0; i < entitlement.getInheritedCount(); i++) {
      chalk.ir.v1.InheritedVisibility declaredPath = entitlement.getInherited(i);
      String endpointSchemaName =
          declaredPath.getEndpointSchema().isEmpty()
              ? schema.getName()
              : declaredPath.getEndpointSchema();
      check(
          converter,
          typeFactory,
          List.of(endpointSchemaName, declaredPath.getEndpointTable()),
          List.of(),
          endpointSchemaName + "." + declaredPath.getEndpointTable(),
          path + ".inherited[" + i + "].endpoint_predicate",
          "the endpoint predicate of the path of kind '" + declaredPath.getKind() + "'",
          declaredPath.getEndpointPredicate(),
          null);

      // And the path predicate over the target's own row with *this* endpoint in scope (D279 §2):
      // it is decided above the join, where both rows are, and a name resolving to neither — another
      // endpoint's column, a parent's — is refused here rather than read at an offset that means
      // something else.
      if (!declaredPath.getPathPredicate().isBlank()) {
        check(
            converter,
            typeFactory,
            qualified,
            List.of(List.of(endpointSchemaName, declaredPath.getEndpointTable())),
            where,
            path + ".inherited[" + i + "].path_predicate",
            "the path predicate of the path of kind '" + declaredPath.getKind() + "'",
            declaredPath.getPathPredicate(),
            null);
      }
    }

    List<ColumnEntitlement> columns = entitlement.getColumnsList();
    // What each rule of each column can hand a principal in place of the value, read off the very
    // expressions this check converts (F58, DisclosedNullability). Keyed by column ordinal, because
    // the widening is answered per column of the row and the descriptor lists only some of them.
    java.util.Map<Integer, ColumnEntitlement> listed = new java.util.LinkedHashMap<>();
    java.util.Map<Integer, Stand> columnMask = new java.util.LinkedHashMap<>();
    java.util.Map<Integer, Stand> columnPlaceholder = new java.util.LinkedHashMap<>();
    java.util.Map<Integer, Stand[]> ruleMasks = new java.util.LinkedHashMap<>();
    java.util.Map<Integer, Stand[]> rulePlaceholders = new java.util.LinkedHashMap<>();
    BitSet protectedColumns =
        protectedColumns(converter, qualified, where, entitlement, rowType.getFieldCount());
    for (int c = 0; c < columns.size(); c++) {
      ColumnEntitlement column = columns.get(c);
      String name =
          column.getColumn() < rowType.getFieldCount()
              ? rowType.getFieldList().get(column.getColumn()).getName()
              : "column " + column.getColumn();
      String columnPath = path + ".columns[" + c + "] (" + name + ")";
      RelDataType declared =
          column.getColumn() < rowType.getFieldCount()
              ? rowType.getFieldList().get(column.getColumn()).getType()
              : null;

      // D190 as D295 extends it, the client's own check made again here: an allow-list names a
      // population aggregate of the fixed set, or an aggregate this catalog declares Population().
      for (int f = 0; f < column.getAggregateOnlyFunctionsCount(); f++) {
        String function = column.getAggregateOnlyFunctions(f);
        if (!PopulationAggregates.isPermitted(function, functions)) {
          throw new InvalidCatalogException(
              columnPath + ".aggregate_only_functions[" + f + "]",
              "'" + function + "' is not a population aggregate (D190). MIN, MAX, ANY_VALUE, the"
                  + " positional and holistic aggregates, every string aggregate and any"
                  + " user-defined aggregate not declared Population() each report an individual"
                  + " row's value. A user-defined aggregate may be named here once its host declares"
                  + " it Population(), promising that its result reports the group and never one"
                  + " row's value (D295).");
        }
      }

      List<DisclosureRule> rules = column.getRulesList();
      Stand[] masksOfRule = new Stand[rules.size()];
      Stand[] placeholdersOfRule = new Stand[rules.size()];
      for (int r = 0; r < rules.size(); r++) {
        // A rule condition may read any column of the row (D220): what it discloses is one bit, and
        // the policy author chose it. It may read the declared parents' columns too (§3.13, D228),
        // which is where the role half of a child's rule lives. Only the value a mask puts in the
        // result is checked below.
        check(converter, typeFactory, qualified, from, where,
            columnPath + ".rules[" + r + "].when", "the rule condition", rules.get(r).getWhen(),
            null);
        RexNode mask =
            checkOwnRow(converter, typeFactory, qualified, parents, where,
                columnPath + ".rules[" + r + "].mask", "mask", rules.get(r).getMask(), declared,
                MASK_IS_A_VALUE);
        reads(mask, column.getColumn(), protectedColumns, rowType, where,
            columnPath + ".rules[" + r + "].mask", "mask", rules.get(r).getMask());
        masksOfRule[r] = stand(rules.get(r).getMask(), mask);

        // A rule's own placeholder (D224): permitted only where the rule redacts, refused where it
        // discloses, and never a reading of the column it stands in place of.
        String rulePlaceholderPath = columnPath + ".rules[" + r + "].placeholder";
        if (!rules.get(r).getPlaceholder().isBlank()
            && normalise(rules.get(r).getThen()) != Disclosure.DISCLOSURE_NONE) {
          throw new InvalidCatalogException(
              rulePlaceholderPath,
              "on " + where + ", the rule for '" + name(rowType, column.getColumn())
                  + "' discloses " + spelling(rules.get(r).getThen()) + " and states a placeholder."
                  + " A placeholder is what stands in for a value the rule withholds, so it is"
                  + " meaningful only with NONE (docs/design/16-entitlements.md §3.11, D224). The"
                  + " text is: " + rules.get(r).getPlaceholder());
        }
        RexNode rulePlaceholder =
            checkOwnRow(converter, typeFactory, qualified, parents, where, rulePlaceholderPath,
                "the placeholder", rules.get(r).getPlaceholder(), declared, MASK_IS_A_VALUE);
        readsOwn(rulePlaceholder, column.getColumn(), rowType, where, rulePlaceholderPath,
            rules.get(r).getPlaceholder());
        reads(rulePlaceholder, column.getColumn(), protectedColumns, rowType, where,
            rulePlaceholderPath, "the placeholder", rules.get(r).getPlaceholder());
        placeholdersOfRule[r] = stand(rules.get(r).getPlaceholder(), rulePlaceholder);

        // The comparison shapes a rule permits (D261, docs/design/36-test-verdict.md §1). A TEST
        // rule naming none discloses neither the value nor any comparison of it; shapes under a
        // verdict that neither tests nor counts could never be reached; and a comparison of a value
        // of a type that has no equality could never be compiled at the leaf.
        checkTests(
            rules.get(r), declared, name(rowType, column.getColumn()), where,
            columnPath + ".rules[" + r + "].tests");
      }

      RexNode mask =
          checkOwnRow(converter, typeFactory, qualified, parents, where, columnPath + ".mask",
              "mask", column.getMask(), declared, MASK_IS_A_VALUE);
      reads(mask, column.getColumn(), protectedColumns, rowType, where, columnPath + ".mask",
          "mask", column.getMask());
      RexNode placeholder =
          checkOwnRow(converter, typeFactory, qualified, parents, where,
              columnPath + ".placeholder", "placeholder", column.getPlaceholder(), declared,
              MASK_IS_A_VALUE);
      readsOwn(placeholder, column.getColumn(), rowType, where, columnPath + ".placeholder",
          column.getPlaceholder());
      reads(placeholder, column.getColumn(), protectedColumns, rowType, where,
          columnPath + ".placeholder", "the placeholder", column.getPlaceholder());

      if (column.getColumn() >= 0 && column.getColumn() < rowType.getFieldCount()) {
        listed.put(column.getColumn(), column);
        columnMask.put(column.getColumn(), stand(column.getMask(), mask));
        columnPlaceholder.put(column.getColumn(), stand(column.getPlaceholder(), placeholder));
        ruleMasks.put(column.getColumn(), masksOfRule);
        rulePlaceholders.put(column.getColumn(), placeholdersOfRule);
      }
    }

    nullability.add(
        schema.getName(),
        table.getName(),
        widened(
            converter, qualified, where, entitlement, rowType, listed, columnMask,
            columnPlaceholder, ruleMasks, rulePlaceholders,
            chalk.planner.rpc.v1.PlaceholderPolicy.PLACEHOLDER_POLICY_AS_NULL),
        widened(
            converter, qualified, where, entitlement, rowType, listed, columnMask,
            columnPlaceholder, ruleMasks, rulePlaceholders,
            chalk.planner.rpc.v1.PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY));
  }

  /** What one field of a descriptor can put in a value's place, as far as the row type cares. */
  private enum Stand {
    /** The field is not stated at all; whatever stands behind it decides. */
    ABSENT,
    /** The field is stated and its value is never NULL. */
    NOT_NULL,
    /** The field is stated and can be NULL — or is a bare context reference, whose type is the
     * host's per request and which is therefore taken as nullable, the conservative direction. */
    NULLABLE
  }

  private static Stand stand(String sql, @Nullable RexNode converted) {
    if (sql == null || sql.isBlank()) {
      return Stand.ABSENT;
    }
    // Null means the check skipped it: a bare `@ctx.<name>`, whose type registration cannot know.
    return converted == null || converted.getType().isNullable() ? Stand.NULLABLE : Stand.NOT_NULL;
  }

  /**
   * The columns of one entitled table that are nullable in the row type a statement is written over,
   * under {@code policy} (16-entitlements.md §1, D161, D162, D224; F58).
   *
   * <p>Read off <em>every</em> rule of the descriptor and never off one principal's folded rules: the
   * row shape is the same for every principal, so a column one role's rule can withhold is nullable
   * for all of them. A column the catalog already declares nullable is left alone, as is one that
   * discloses {@code FULL} unconditionally (V127) — the case a host writes to say "this column is
   * not sensitive" on a table whose default hides everything else.
   */
  private static org.apache.calcite.util.ImmutableBitSet widened(
      DescriptorConverter converter,
      List<String> qualified,
      String where,
      TableEntitlement entitlement,
      RelDataType rowType,
      java.util.Map<Integer, ColumnEntitlement> listed,
      java.util.Map<Integer, Stand> columnMask,
      java.util.Map<Integer, Stand> columnPlaceholder,
      java.util.Map<Integer, Stand[]> ruleMasks,
      java.util.Map<Integer, Stand[]> rulePlaceholders,
      chalk.planner.rpc.v1.PlaceholderPolicy policy) {
    org.apache.calcite.util.ImmutableBitSet.Builder widened =
        org.apache.calcite.util.ImmutableBitSet.builder();
    Disclosure byDefault = defaultDisclosure(entitlement);

    for (int c = 0; c < rowType.getFieldCount(); c++) {
      RelDataType declared = rowType.getFieldList().get(c).getType();
      if (declared.isNullable()) {
        // Already nullable: the statement's expressions are simplified under a nullable column
        // whatever the policy hands back, which is the property F58 is about.
        continue;
      }

      ColumnEntitlement column = listed.get(c);
      if (column == null) {
        // A column the descriptor does not list takes `default_disclosure` (D159). FULL leaves it
        // alone; NONE withholds it with no stand-in of its own, so the request's policy decides.
        if (normalise(byDefault) == Disclosure.DISCLOSURE_NONE
            && withheld(Stand.ABSENT, Stand.ABSENT, declared, policy)) {
          widened.set(c);
        }
        continue;
      }
      if (unconditionallyFull(converter, qualified, where, column)) {
        continue;
      }

      Stand[] masks = ruleMasks.getOrDefault(c, new Stand[0]);
      Stand[] placeholders = rulePlaceholders.getOrDefault(c, new Stand[0]);
      Stand ownMask = columnMask.getOrDefault(c, Stand.ABSENT);
      Stand ownPlaceholder = columnPlaceholder.getOrDefault(c, Stand.ABSENT);
      boolean widens = false;
      List<DisclosureRule> rules = column.getRulesList();
      for (int r = 0; r < rules.size() && !widens; r++) {
        widens =
            switch (normalise(rules.get(r).getThen())) {
              // The stored value itself, which is NOT NULL here.
              case DISCLOSURE_FULL, DISCLOSURE_AGGREGATE_ONLY -> false;
              case DISCLOSURE_MASKED ->
                  masked(r < masks.length ? masks[r] : Stand.ABSENT, ownMask);
              default ->
                  withheld(
                      r < placeholders.length ? placeholders[r] : Stand.ABSENT,
                      ownPlaceholder,
                      declared,
                      policy);
            };
      }
      if (!widens) {
        widens =
            switch (normalise(column.getOtherwise())) {
              case DISCLOSURE_FULL, DISCLOSURE_AGGREGATE_ONLY -> false;
              case DISCLOSURE_MASKED -> masked(Stand.ABSENT, ownMask);
              default -> withheld(Stand.ABSENT, ownPlaceholder, declared, policy);
            };
      }
      if (widens) {
        widened.set(c);
      }
    }
    return widened.build();
  }

  /**
   * Whether a {@code MASKED} rule can hand back NULL: its own mask where it states one, else the
   * column's. A rule that reaches {@code MASKED} with no mask anywhere is a {@code POLICY} refusal
   * at planning (§3.2), so there is no value for the row type to describe and it widens nothing.
   */
  private static boolean masked(Stand ofRule, Stand ofColumn) {
    Stand mask = ofRule == Stand.ABSENT ? ofColumn : ofRule;
    return mask == Stand.NULLABLE;
  }

  /**
   * Whether a {@code NONE} rule can hand back NULL: the rule's own placeholder, then the column's,
   * then the request's {@code PlaceholderPolicy} — a typed NULL, or under {@code PlaceholdersAsEmpty}
   * the type's empty value where Calcite defines one and a typed NULL where it does not (D224, F47).
   */
  private static boolean withheld(
      Stand ofRule, Stand ofColumn, RelDataType declared,
      chalk.planner.rpc.v1.PlaceholderPolicy policy) {
    Stand stated = ofRule == Stand.ABSENT ? ofColumn : ofRule;
    if (stated != Stand.ABSENT) {
      return stated == Stand.NULLABLE;
    }
    return policy != chalk.planner.rpc.v1.PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY
        || !EntitlementPass.hasEmptyValue(declared);
  }

  /** {@code UNSPECIFIED} reads as {@code FULL} for the table's default, exactly as the pass does. */
  private static Disclosure defaultDisclosure(TableEntitlement entitlement) {
    Disclosure declared = entitlement.getDefaultDisclosure();
    return declared == Disclosure.DISCLOSURE_UNSPECIFIED || declared == Disclosure.UNRECOGNIZED
        ? Disclosure.DISCLOSURE_FULL
        : declared;
  }

  /**
   * Why a mask and a placeholder may not read a parent's column where a rule condition may
   * (§3.13, D228 as built; ADR 0025 part 2h).
   */
  private static final String MASK_IS_A_VALUE =
      "A mask and a placeholder are values this table hands back, evaluated over its own raw row, "
          + "and the parent's row is not on it: through an unmatched LEFT JOIN there would be none, "
          + "and through two occurrences of one parent there would be two. A rule *condition* may "
          + "read the parent's columns, because what it discloses is one bit and the pass evaluates "
          + "it on the parent's own side";

  /**
   * One field that must be over the child's <em>own</em> row: converted with the declared parents
   * out of scope, so a reference to one does not resolve at all (§3.13).
   *
   * <p>The refusal is certain either way; what the parent's name in the text decides is only the
   * wording, and a message that says "the parent's row is not on this one" helps where "object not
   * found" would puzzle.
   */
  private static @Nullable RexNode checkOwnRow(
      DescriptorConverter converter,
      RelDataTypeFactory typeFactory,
      List<String> qualified,
      ThroughParents parents,
      String where,
      String field,
      String what,
      String sql,
      @Nullable RelDataType expected,
      String why) {
    try {
      return check(
          converter, typeFactory, qualified, List.of(), where, field, what, sql, expected);
    } catch (InvalidCatalogException refusal) {
      String parent = parents.mentionedIn(sql);
      if (parent == null) {
        throw refusal;
      }
      throw new InvalidCatalogException(
          field,
          "on " + where + ", " + what + " reads '" + parent + "', a parent this table is entitled"
              + " through. " + why + " (docs/design/16-entitlements.md §3.13, D225, D228). The"
              + " text is: " + sql);
    }
  }

  /**
   * The columns this descriptor protects: every one carrying an entitlement, and — under {@code
   * default_disclosure = NONE} — every column at all, since one this descriptor does not list is
   * then withheld too (D159, D220, V127).
   *
   * <p><b>With one refinement.</b> Under a {@code NONE} default a listed column whose disclosure is
   * <em>unconditionally</em> {@code FULL} — its first rule's condition is TRUE and discloses FULL,
   * or it has no rules and an {@code otherwise} of FULL where D208 permits one — is protected by
   * nothing, so a mask may read it exactly as it may read any column of an unprotected table. That
   * is the case the host writes to say "this column is not sensitive" on a table whose default
   * hides everything else, and treating it as protected refused a legitimate mask. A column FULL
   * for <em>some</em> roles only stays protected: what a principal without those roles sees of it
   * is a placeholder, and a mask reading it would disclose what the report says it did not.
   */
  private static BitSet protectedColumns(
      DescriptorConverter converter,
      List<String> qualified,
      String where,
      TableEntitlement entitlement,
      int width) {
    BitSet columns = new BitSet(width);
    if (entitlement.getDefaultDisclosure() == Disclosure.DISCLOSURE_NONE) {
      columns.set(0, width);
      for (ColumnEntitlement column : entitlement.getColumnsList()) {
        if (column.getColumn() >= 0
            && column.getColumn() < width
            && unconditionallyFull(converter, qualified, where, column)) {
          columns.clear(column.getColumn());
        }
      }
      return columns;
    }
    for (ColumnEntitlement column : entitlement.getColumnsList()) {
      if (column.getColumn() >= 0 && column.getColumn() < width) {
        columns.set(column.getColumn());
      }
    }
    return columns;
  }

  /** Whether this column discloses {@code FULL} for every row and every principal (V127). */
  private static boolean unconditionallyFull(
      DescriptorConverter converter, List<String> qualified, String where, ColumnEntitlement column) {
    if (column.getRulesCount() == 0) {
      return column.getOtherwise() == Disclosure.DISCLOSURE_FULL;
    }
    DisclosureRule first = column.getRules(0);
    if (first.getThen() != Disclosure.DISCLOSURE_FULL || first.getWhen().isBlank()) {
      return false;
    }
    try {
      RexNode condition =
          converter.convert(List.of("(" + first.getWhen() + ")"), qualified, List.of(), where).get(0);
      RexSimplify simplify =
          new RexSimplify(
              new RexBuilder(converter.typeFactory()),
              org.apache.calcite.plan.RelOptPredicateList.EMPTY,
              org.apache.calcite.rex.RexUtil.EXECUTOR);
      return simplify.simplifyUnknownAsFalse(condition).isAlwaysTrue();
    } catch (RuntimeException notConvertible) {
      // The field's own check reports it; here an unconvertible condition simply is not TRUE.
      return false;
    }
  }

  /** {@code UNSPECIFIED} reads as {@code NONE}, exactly as the pass reads it (§3.2). */
  private static Disclosure normalise(Disclosure declared) {
    return declared == Disclosure.DISCLOSURE_UNSPECIFIED || declared == Disclosure.UNRECOGNIZED
        ? Disclosure.DISCLOSURE_NONE
        : declared;
  }

  /** A disclosure as the descriptor's own vocabulary spells it: {@code MASKED}, not the enum name. */
  private static String spelling(Disclosure declared) {
    return normalise(declared).name().replace("DISCLOSURE_", "");
  }

  /**
   * The comparison shapes one rule permits (D261, docs/design/36-test-verdict.md §1).
   *
   * <p>Three refusals, each of a rule that could never be honoured whatever the SQL says: a
   * {@code TEST} rule that names no shape, shapes on a verdict that reads none, and a test of a
   * column whose type states no equality — which is the {@code LIST} kind and the unspecified one
   * (types.proto, D58). The client's own {@code CatalogValidator} makes the same three refusals
   * before any RPC; this is the side that answers for a descriptor a host wrote itself.
   */
  private static void checkTests(
      DisclosureRule rule,
      @Nullable RelDataType declared,
      String column,
      String where,
      String path) {
    Disclosure then = normalise(rule.getThen());
    if (then == Disclosure.DISCLOSURE_TEST && rule.getTestsCount() == 0) {
      throw new InvalidCatalogException(
          path,
          "on " + where + ", the rule for '" + column + "' discloses TEST and names no comparison"
              + " shape, so it discloses neither the value nor any comparison of it"
              + " (docs/design/36-test-verdict.md §1). Name the shapes — EQUALS, NOT_EQUALS, IN —"
              + " or say NONE and mean it.");
    }

    if (rule.getTestsCount() > 0
        && then != Disclosure.DISCLOSURE_TEST
        && then != Disclosure.DISCLOSURE_AGGREGATE_ONLY) {
      throw new InvalidCatalogException(
          path,
          "on " + where + ", the rule for '" + column + "' discloses " + spelling(rule.getThen())
              + " and names comparison shapes, which could never be reached. A shape is what a"
              + " principal may test without reading the value, so it belongs on a TEST rule, or on"
              + " an AGGREGATE_ONLY rule as the FILTER of a permitted aggregate (D261).");
    }

    for (int t = 0; t < rule.getTestsCount(); t++) {
      if (rule.getTests(t) == chalk.ir.v1.TestShape.TEST_SHAPE_UNSPECIFIED
          || rule.getTests(t) == chalk.ir.v1.TestShape.UNRECOGNIZED) {
        throw new InvalidCatalogException(
            path + "[" + t + "]",
            "on " + where + ", the rule for '" + column + "' names a comparison shape that is not"
                + " one of the three. Say EQUALS, NOT_EQUALS or IN.");
      }
    }

    if (rule.getTestsCount() > 0 && declared != null && !hasEquality(declared)) {
      throw new InvalidCatalogException(
          path,
          "on " + where + ", the rule permits a comparison of '" + column + "', which is "
              + declared.getSqlTypeName()
              + " — a type Chalk states no equality for (D58), so no comparison of it could be"
              + " computed at the leaf. A test verdict needs a column whose values compare.");
    }
  }

  /** Whether a value of this type compares for equality at all (types.proto, D58). */
  private static boolean hasEquality(RelDataType type) {
    return type.getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.ARRAY
        && type.getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.MULTISET
        && type.getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.MAP
        && type.getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.ROW;
  }

  /**
   * Refuses a mask or a placeholder that reads another protected column, naming both (D220).
   *
   * @param expression the converted field, or null where there was nothing to convert
   * @param own the ordinal of the column being masked, which the expression is of course free to
   *     read: that is what a mask is
   */
  private static void reads(
      @Nullable RexNode expression,
      int own,
      BitSet protectedColumns,
      RelDataType rowType,
      String where,
      String field,
      String what,
      String sql) {
    if (expression == null) {
      return;
    }
    for (int column : RelOptUtil.InputFinder.bits(expression)) {
      if (column == own || !protectedColumns.get(column)) {
        continue;
      }
      throw new InvalidCatalogException(
          field,
          "on " + where + ", " + what + " for '" + name(rowType, own) + "' reads '"
              + name(rowType, column) + "', which is itself a protected column. A mask is evaluated"
              + " over the raw row, so this one would disclose '" + name(rowType, column)
              + "' inside '" + name(rowType, own) + "' to a principal entitled to neither"
              + " (docs/design/16-entitlements.md §1, D220). A mask may read its own column, an"
              + " unprotected column and the context; a rule condition may read anything. The text"
              + " is: " + sql);
    }
  }

  /**
   * Refuses a placeholder that reads the column it stands in place of, naming it (D224).
   *
   * <p>A mask may read its own column — that is what a mask <em>is</em>. A placeholder may not: the
   * outcome under it stays REDACTED, and a placeholder holding the very value the rule withholds
   * would disclose it while the report says it did not. The two look alike and mean opposite things,
   * which is the whole reason this check exists beside the one above.
   */
  private static void readsOwn(
      @Nullable RexNode expression,
      int own,
      RelDataType rowType,
      String where,
      String field,
      String sql) {
    if (expression == null || !RelOptUtil.InputFinder.bits(expression).get(own)) {
      return;
    }
    throw new InvalidCatalogException(
        field,
        "on " + where + ", the placeholder for '" + name(rowType, own) + "' reads '"
            + name(rowType, own) + "' itself. A placeholder stands in for a value that is withheld,"
            + " and the disclosure stays REDACTED, so one holding the column's own value would"
            + " disclose exactly what it claims to have withheld"
            + " (docs/design/16-entitlements.md §3.11, D224). A rule that means to show something"
            + " derived from the value discloses MASKED and states a mask. The text is: " + sql);
  }

  /** A column of the row by ordinal, or its ordinal where the row is narrower than the descriptor. */
  private static String name(RelDataType rowType, int column) {
    return column >= 0 && column < rowType.getFieldCount()
        ? rowType.getFieldList().get(column).getName()
        : "column " + column;
  }

  /**
   * One field: converted alone, so a failure names it.
   *
   * @param what the field as a message names it: "the rule condition", "mask"
   * @param expected the type the field must have, or null for a field that must be boolean
   */
  private static @Nullable RexNode check(
      DescriptorConverter converter,
      RelDataTypeFactory typeFactory,
      List<String> qualified,
      List<List<String>> parents,
      String where,
      String field,
      String what,
      String sql,
      @Nullable RelDataType expected) {
    // Before the fold and before the parser (D223): a descriptor naming one of the planner's own
    // markers would be indistinguishable from the marker itself once the rewrite has run.
    try {
      chalk.planner.ReservedNames.check(sql, what);
    } catch (chalk.planner.ReservedNames.ReservedNameException reserved) {
      throw new InvalidCatalogException(field, "on " + where + ", " + reserved.getMessage());
    }

    if (sql.isBlank() || RegistrationFold.isBareContextReference(sql)) {
      // Nothing to check: an absent field, or one that is a context value and nothing else, whose
      // type the host declares per request and registration never sees.
      return null;
    }

    RexNode converted;
    try {
      converted = converter.convert(List.of("(" + sql + ")"), qualified, parents, where).get(0);
    } catch (PolicyException refusal) {
      throw new InvalidCatalogException(
          field, refusal.getMessage() + " The text is: " + sql);
    }

    RelDataType actual = converted.getType();
    if (expected == null) {
      if (!SqlTypeUtil.inBooleanFamily(actual)) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", " + what + " is not boolean but " + name(typeFactory, actual)
                + " (docs/design/16-entitlements.md §1). The text is: " + sql);
      }
      return converted;
    }

    if (!assignable(expected, actual)) {
      throw new InvalidCatalogException(
          field,
          "on " + where + ", " + what + " type " + name(typeFactory, actual)
              + " does not match column type " + name(typeFactory, expected)
              + " (docs/design/16-entitlements.md §1: a mask and a placeholder have the column's"
              + " type, so every principal gets the same row shape). The text is: " + sql);
    }

    return converted;
  }

  /**
   * A type as the host named it, not as Calcite spells it: the descriptor was written in Chalk's
   * own vocabulary and a message about it should answer in the same one. Calcite's name is the
   * fallback for a type the IR has no name for, which a descriptor cannot produce but an operator
   * table could.
   */
  private static String name(RelDataTypeFactory typeFactory, RelDataType type) {
    try {
      return new chalk.planner.types.TypeMapper(typeFactory)
          .toIr(type)
          .getKind()
          .name()
          .replace("TYPE_KIND_", "");
    } catch (RuntimeException unnamed) {
      return type.getSqlTypeName().getName();
    }
  }

  /**
   * Whether a mask or a placeholder of type {@code actual} may stand for a column of type
   * {@code declared} — <em>without</em> the implicit coercion the validator would apply in a
   * position that had a target type.
   *
   * <p>A bare NULL fits anything, a number fits a number so that {@code -1} stands for a
   * {@code DECIMAL(10,2)}, and a character string fits a character string whatever its width. Every
   * other pair is refused, which is what turns {@code '****'} on a DATE column from a run-time cast
   * failure into a message naming the column.
   */
  private static boolean assignable(RelDataType declared, RelDataType actual) {
    SqlTypeName mine = actual.getSqlTypeName();
    if (mine == SqlTypeName.NULL) {
      return true;
    }
    if (declared.getSqlTypeName() == mine) {
      return true;
    }
    if (SqlTypeUtil.isNumeric(declared) && SqlTypeUtil.isNumeric(actual)) {
      return true;
    }
    return SqlTypeUtil.isCharacter(declared) && SqlTypeUtil.isCharacter(actual);
  }
}
