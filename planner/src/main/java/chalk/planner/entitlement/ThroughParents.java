package chalk.planner.entitlement;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.InheritedVisibility;
import chalk.ir.v1.ParentVisibility;
import chalk.ir.v1.StepDirection;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.UniqueKey;
import chalk.ir.v1.VisibilityStep;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.catalog.InvalidCatalogException;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One entitled table's declared parents, resolved against the catalog
 * (docs/design/16-entitlements.md §3.13, D225).
 *
 * <p>Two things come out of it. The <b>shape checks</b> of D225 — the parent exists, is entitled and
 * is itself restricted, its key is a declared unique key, the child's column has the key's type
 * kind, and no chain of parents returns to a table — each refused with a message naming what is
 * wrong. And the <b>column blocks</b>: the descriptor's SQL is converted over
 * {@code SELECT … FROM <child>, <parent…>} so that a rule condition's {@code <parent_table>.<column>}
 * resolves at all, and this is what says which converted {@code RexInputRef} belongs to which
 * parent.
 *
 * <p>A parent table named twice among the entries — the same parent through two columns — is
 * <em>one</em> block and two entries: the rule conditions are written once and evaluated at each
 * occurrence, which is what D228's "the least of the parents' ordinals" combines.
 */
public final class ThroughParents {

  private static final ThroughParents NONE = new ThroughParents(List.of());

  /** The distinct parent tables, in the order their column blocks follow the child's. */
  private final List<List<String>> blocks;

  private ThroughParents(List<List<String>> blocks) {
    this.blocks = blocks;
  }

  public boolean isEmpty() {
    return blocks.isEmpty();
  }

  /** The parent tables as the generated statement's {@code FROM} names them, in block order. */
  public List<List<String>> qualifiedNames() {
    return blocks;
  }

  /**
   * The first declared parent this text names as a qualifier, or null when it names none.
   *
   * <p>Used only to word a refusal that has already been decided (a field over the child's own row
   * that did not resolve with the parents out of scope), never to decide one — so a false positive
   * would change a message and a false negative would leave the general message in place.
   */
  public @Nullable String mentionedIn(String sql) {
    String lower = sql.toLowerCase(Locale.ROOT);
    for (List<String> block : blocks) {
      String table = block.get(block.size() - 1);
      String name = table.toLowerCase(Locale.ROOT);
      int at = lower.indexOf(name + ".");
      while (at >= 0) {
        if (at == 0 || !isNameChar(lower.charAt(at - 1))) {
          return table;
        }
        at = lower.indexOf(name + ".", at + 1);
      }
    }
    return null;
  }

  private static boolean isNameChar(char c) {
    return Character.isLetterOrDigit(c) || c == '_' || c == '$' || c == '"';
  }

  // ------------------------------------------------------------------ resolution

  /**
   * Resolves and checks {@code table}'s {@code through} entries.
   *
   * @throws InvalidCatalogException naming the entry and what is wrong with it
   */
  public static ThroughParents resolve(
      CatalogContext catalog,
      Schema schema,
      Table table,
      RelDataTypeFactory typeFactory,
      String path) {
    TableEntitlement entitlement = table.getEntitlement();
    if (entitlement.getThroughCount() == 0 && entitlement.getInheritedCount() == 0) {
      return NONE;
    }

    RelDataType childRow = new ChalkTable(table, schema).getRowType(typeFactory);
    Map<String, List<String>> blocks = new LinkedHashMap<>();
    Map<String, String> byAlias = new LinkedHashMap<>();

    for (int i = 0; i < entitlement.getThroughCount(); i++) {
      ParentVisibility through = entitlement.getThrough(i);
      String field = path + ".through[" + i + "]";
      String where = schema.getName() + "." + table.getName();

      if (through.getColumn() >= childRow.getFieldCount()) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the correlation column " + through.getColumn()
                + " is out of range for a table of " + childRow.getFieldCount() + " columns.");
      }

      String parentSchemaName =
          through.getParentSchema().isEmpty() ? schema.getName() : through.getParentSchema();
      Schema parentSchema = schemaNamed(catalog, parentSchemaName);
      Table parent =
          parentSchema == null ? null : tableNamed(parentSchema, through.getParentTable());
      if (parent == null) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", visibility derives through '" + parentSchemaName + "."
                + through.getParentTable() + "', which this catalog does not hold. A `through`"
                + " names a table of the same catalog, so the pass can compile it into one join"
                + " against that table's own entitled scan"
                + " (docs/design/16-entitlements.md §3.13, D225).");
      }

      if (!parent.hasEntitlement()) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", visibility derives through '" + parentSchemaName + "."
                + parent.getName() + "', which carries no entitlement. Through an unrestricted"
                + " parent restricts nothing; declare the child unrestricted or restrict the"
                + " parent (§3.13, D225).");
      }

      TableEntitlement parentEntitlement = parent.getEntitlement();
      if (parentEntitlement.getRowPredicate().isBlank()
          && parentEntitlement.getThroughCount() == 0) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", visibility derives through '" + parentSchemaName + "."
                + parent.getName() + "', whose entitlement restricts no row — it has neither a row"
                + " predicate nor a `through` of its own. Through an unrestricted parent restricts"
                + " nothing; declare the child unrestricted or restrict the parent (§3.13, D225).");
      }

      RelDataType parentRow = new ChalkTable(parent, parentSchema).getRowType(typeFactory);
      if (through.getParentColumn() >= parentRow.getFieldCount()) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the parent key column " + through.getParentColumn()
                + " is out of range for '" + parentSchemaName + "." + parent.getName()
                + "', which has " + parentRow.getFieldCount() + " columns.");
      }

      String parentKey = parentRow.getFieldList().get(through.getParentColumn()).getName();
      if (!isDeclaredUniqueKey(parent, through.getParentColumn())) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", '" + parentSchemaName + "." + parent.getName() + "." + parentKey
                + "' is not a declared unique key of the parent. The join a `through` compiles into"
                + " must not multiply rows, and a unique key is what makes that so (§3.13, D226)."
                + " Declare the key.");
        }

      String childKey = childRow.getFieldList().get(through.getColumn()).getName();
      RelDataType childType = childRow.getFieldList().get(through.getColumn()).getType();
      RelDataType parentType = parentRow.getFieldList().get(through.getParentColumn()).getType();
      if (childType.getSqlTypeName() != parentType.getSqlTypeName()) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the correlation key '" + childKey + "' is "
                + childType.getSqlTypeName() + " and the parent key '" + parentKey + "' is "
                + parentType.getSqlTypeName() + "; they must have the same type kind, or the join"
                + " could never match (§3.13, D225).");
      }

      // Two parents that arrive under the same unqualified name are two things a rule condition
      // would spell the same way, and neither the validator nor the pass could tell them apart.
      String alias = parent.getName().toLowerCase(Locale.ROOT);
      String qualifiedName = parentSchemaName + "." + parent.getName();
      String already = byAlias.putIfAbsent(alias, qualifiedName);
      if (already != null && !already.equals(qualifiedName)) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", two parents are both named '" + parent.getName() + "' ('" + already
                + "' and '" + qualifiedName + "'). A rule condition names a parent's column as"
                + " <parent_table>.<column>, so two parents of one name could not be told apart"
                + " (§3.13, D225).");
      }
      if (alias.equals(table.getName().toLowerCase(Locale.ROOT))) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the parent is named '" + parent.getName() + "', which is this"
                + " table's own name; a rule condition could not tell <parent_table>.<column> from"
                + " this table's own column (§3.13, D225).");
      }

      blocks.putIfAbsent(qualifiedName, List.of(parentSchemaName, parent.getName()));
    }

    resolveInherited(catalog, schema, table, typeFactory, path, childRow, blocks, byAlias);
    refuseCycle(catalog, schema, table, new ArrayList<>(), path);
    return new ThroughParents(List.copyOf(blocks.values()));
  }

  /**
   * One entitled table's declared paths, resolved against the catalog and given their column blocks
   * (D265, docs/design/38-existential-visibility.md §3).
   *
   * <p>Every step arrives at a table the catalog holds, over a declared foreign key in the direction
   * the step claims; the shape is one §1 admits; the bridge's two key columns carry no rule; and
   * neither a bridge nor the endpoint is the target itself. The <b>endpoint</b> tables' blocks follow
   * the {@code through} parents' in the same {@code FROM}, so a rule condition naming
   * {@code <endpoint_table>.<column>} resolves exactly as one naming a parent's does.
   */
  private static void resolveInherited(
      CatalogContext catalog,
      Schema schema,
      Table table,
      RelDataTypeFactory typeFactory,
      String path,
      RelDataType childRow,
      Map<String, List<String>> blocks,
      Map<String, String> byAlias) {
    TableEntitlement entitlement = table.getEntitlement();
    String where = schema.getName() + "." + table.getName();

    for (int i = 0; i < entitlement.getInheritedCount(); i++) {
      InheritedVisibility declared = entitlement.getInherited(i);
      String field = path + ".inherited[" + i + "]";

      if (declared.getStepsCount() == 0) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the path of kind '" + declared.getKind() + "' has no step. A path"
                + " with no step is a direct dimension written the long way round"
                + " (docs/design/38-existential-visibility.md §1, §3, D265).");
      }

      Schema fromSchema = schema;
      Table fromTable = table;
      RelDataType fromRow = childRow;
      for (int s = 0; s < declared.getStepsCount(); s++) {
        VisibilityStep step = declared.getSteps(s);
        String stepSchemaName = step.getSchema().isEmpty() ? schema.getName() : step.getSchema();
        Schema stepSchema = schemaNamed(catalog, stepSchemaName);
        Table to = stepSchema == null ? null : tableNamed(stepSchema, step.getTable());
        if (to == null) {
          throw new InvalidCatalogException(
              field,
              "on " + where + ", step " + s + " of the path of kind '" + declared.getKind()
                  + "' arrives at '" + stepSchemaName + "." + step.getTable() + "', which this"
                  + " catalog does not hold. A path names tables of the same catalog, so the pass"
                  + " can compile it into one key set (§3, D265).");
        }

        if (to.getName().equalsIgnoreCase(table.getName())
            && stepSchemaName.equalsIgnoreCase(schema.getName())) {
          throw new InvalidCatalogException(
              field,
              "on " + where + ", step " + s + " of the path of kind '" + declared.getKind()
                  + "' returns to this table itself. A row's visibility cannot derive from its own"
                  + " table's, so a bridge and an endpoint are both other tables (§3, D265).");
        }

        if (step.getDirection() == StepDirection.STEP_DIRECTION_TO_CHILD && s > 0) {
          throw new InvalidCatalogException(
              field,
              "on " + where + ", step " + s + " of the path of kind '" + declared.getKind()
                  + "' goes up. This run admits an inherited path of any length and a related path"
                  + " of one up-step followed by down-steps; a second up-step, and an up-step after"
                  + " a down-step, are refused rather than built (§1, §9, D265).");
        }

        RelDataType toRow = new ChalkTable(to, stepSchema).getRowType(typeFactory);
        checkStep(
            catalog, declared, s, fromSchema, fromTable, fromRow, stepSchema, to, toRow, step, field,
            where);

        if (step.getDirection() == StepDirection.STEP_DIRECTION_TO_CHILD) {
          // The bridge's two key columns are correlation keys the mechanism reads raw, exactly as
          // D227 leaves a parent's key FULL. A rule over either is a contradiction: the policy would
          // be withholding a value the mechanism must read.
          refuseProtectedBridgeKey(to, stepSchemaName, toRow, step.getToColumn(), field, where);
          if (s + 1 < declared.getStepsCount()) {
            refuseProtectedBridgeKey(
                to, stepSchemaName, toRow, declared.getSteps(s + 1).getFromColumn(), field, where);
          }
        }

        fromSchema = stepSchema;
        fromTable = to;
        fromRow = toRow;
      }

      String endpointSchemaName =
          declared.getEndpointSchema().isEmpty() ? schema.getName() : declared.getEndpointSchema();
      if (!fromTable.getName().equalsIgnoreCase(declared.getEndpointTable())) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the path of kind '" + declared.getKind() + "' says its endpoint is '"
                + declared.getEndpointTable() + "', and its last step arrives at '"
                + fromTable.getName() + "'. The endpoint names where the kind is held, so it is the"
                + " table the last step reaches (§2, D265).");
      }

      if (declared.getEndpointPredicate().isBlank()) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the path of kind '" + declared.getKind() + "' carries no endpoint"
                + " predicate. The endpoint '" + fromTable.getName() + "' is where the kind is held,"
                + " and the predicate is what says which of its rows this principal holds it in; a"
                + " path without one would grant every row (§2, §3, D265).");
      }

      String alias = fromTable.getName().toLowerCase(Locale.ROOT);
      String qualifiedName = endpointSchemaName + "." + fromTable.getName();
      String already = byAlias.putIfAbsent(alias, qualifiedName);
      if (already != null && !already.equals(qualifiedName)) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", two tables this one derives visibility from are both named '"
                + fromTable.getName() + "' ('" + already + "' and '" + qualifiedName + "'). A rule"
                + " condition names a column as <table>.<column>, so two of one name could not be"
                + " told apart (§3, D265).");
      }
      if (alias.equals(table.getName().toLowerCase(Locale.ROOT))) {
        throw new InvalidCatalogException(
            field,
            "on " + where + ", the endpoint is named '" + fromTable.getName() + "', which is this"
                + " table's own name; a rule condition could not tell <table>.<column> from this"
                + " table's own column (§3, D265).");
      }

      blocks.putIfAbsent(qualifiedName, List.of(endpointSchemaName, fromTable.getName()));
    }
  }

  /** One step: the declared foreign key exists, in the direction the step claims (§1, §3). */
  private static void checkStep(
      CatalogContext catalog,
      InheritedVisibility declared,
      int ordinal,
      Schema fromSchema,
      Table from,
      RelDataType fromRow,
      Schema toSchema,
      Table to,
      RelDataType toRow,
      VisibilityStep step,
      String field,
      String where) {
    if (step.getFromColumn() >= fromRow.getFieldCount()) {
      throw new InvalidCatalogException(
          field,
          "on " + where + ", step " + ordinal + " of the path of kind '" + declared.getKind()
              + "' reads column " + step.getFromColumn() + " of '" + fromSchema.getName() + "."
              + from.getName() + "', which has " + fromRow.getFieldCount() + " columns.");
    }
    if (step.getToColumn() >= toRow.getFieldCount()) {
      throw new InvalidCatalogException(
          field,
          "on " + where + ", step " + ordinal + " of the path of kind '" + declared.getKind()
              + "' reads column " + step.getToColumn() + " of '" + toSchema.getName() + "."
              + to.getName() + "', which has " + toRow.getFieldCount() + " columns.");
    }

    boolean down = step.getDirection() != StepDirection.STEP_DIRECTION_TO_CHILD;
    Table child = down ? from : to;
    RelDataType childRow = down ? fromRow : toRow;
    int childColumn = down ? step.getFromColumn() : step.getToColumn();
    Table parent = down ? to : from;
    RelDataType parentRow = down ? toRow : fromRow;
    int parentColumn = down ? step.getToColumn() : step.getFromColumn();

    if (!isDeclaredUniqueKey(parent, parentColumn)) {
      throw new InvalidCatalogException(
          field,
          "on " + where + ", step " + ordinal + " of the path of kind '" + declared.getKind()
              + "' joins '" + parent.getName() + "."
              + parentRow.getFieldList().get(parentColumn).getName() + "', which is not a declared"
              + " unique key of that table. The joins a path compiles into must not multiply rows,"
              + " and a unique key is what makes that so (§3, D265). Declare the key.");
    }

    Schema childSchema = down ? fromSchema : toSchema;
    Schema parentSchema = down ? toSchema : fromSchema;
    if (!referencesKey(child, childColumn, parent.getName(), parentColumn)
        && !associates(
            catalog,
            childSchema,
            child,
            childRow.getFieldList().get(childColumn).getName(),
            parentSchema,
            parent,
            parentRow.getFieldList().get(parentColumn).getName())) {
      throw new InvalidCatalogException(
          field,
          "on " + where + ", step " + ordinal + " of the path of kind '" + declared.getKind()
              + "' goes " + (down ? "down" : "up") + " from '" + from.getName() + "' to '"
              + to.getName() + "', and '" + child.getName() + "."
              + childRow.getFieldList().get(childColumn).getName() + "' declares no foreign key"
              + " naming '" + parent.getName() + "."
              + parentRow.getFieldList().get(parentColumn).getName() + "', and this catalog declares"
              + " no association between them either. The direction is checked and never inferred,"
              + " so the association has to be declared before a path can rely on it (§1, §3, D265;"
              + " docs/design/45-typed-tenancy-surface.md §3, D270).");
    }
  }

  /**
   * Whether a declared association states this step's join (D270 (c),
   * docs/design/45-typed-tenancy-surface.md §3).
   *
   * <p>A foreign key is one source's claim about a table of its own schema, so two tables in two
   * sources have nothing to state the association with; the catalog carries it instead. It is the
   * host's assertion and is not verified here, exactly as {@code Table.foreign_keys} is the source's
   * own (ADR 0025's "verified is read as declared") — what is checked is the shape, above.
   */
  private static boolean associates(
      CatalogContext catalog,
      Schema childSchema,
      Table child,
      String childColumn,
      Schema parentSchema,
      Table parent,
      String parentColumn) {
    for (chalk.ir.v1.Association association : catalog.getAssociationsList()) {
      if (association.getFromSchema().equalsIgnoreCase(childSchema.getName())
          && association.getFromTable().equalsIgnoreCase(child.getName())
          && association.getFromColumn().equalsIgnoreCase(childColumn)
          && association.getToSchema().equalsIgnoreCase(parentSchema.getName())
          && association.getToTable().equalsIgnoreCase(parent.getName())
          && association.getToColumn().equalsIgnoreCase(parentColumn)) {
        return true;
      }
    }
    return false;
  }

  private static void refuseProtectedBridgeKey(
      Table via,
      String viaSchemaName,
      RelDataType viaRow,
      int column,
      String field,
      String where) {
    if (!via.hasEntitlement() || column >= viaRow.getFieldCount()) {
      return;
    }
    TableEntitlement entitlement = via.getEntitlement();
    boolean named = false;
    for (chalk.ir.v1.ColumnEntitlement entitled : entitlement.getColumnsList()) {
      named |= entitled.getColumn() == column;
    }
    boolean deniedByDefault =
        entitlement.getDefaultDisclosure() != chalk.ir.v1.Disclosure.DISCLOSURE_FULL
            && entitlement.getDefaultDisclosure() != chalk.ir.v1.Disclosure.DISCLOSURE_UNSPECIFIED;
    if (named || deniedByDefault) {
      throw new InvalidCatalogException(
          field,
          "on " + where + ", the bridge key '" + viaSchemaName + "." + via.getName() + "."
              + viaRow.getFieldList().get(column).getName() + "' is protected by a rule or by its"
              + " table's default. The mechanism reads the bridge's two key columns raw and"
              + " discloses neither, exactly as a parent's key is left FULL under D227, so a rule"
              + " over one is a contradiction: leave it full, or reach the endpoint another way"
              + " (§3, D265).");
    }
  }

  private static boolean referencesKey(
      Table table, int column, String parentTable, int parentColumn) {
    for (chalk.ir.v1.ForeignKey key : table.getForeignKeysList()) {
      if (key.getColumnsCount() == 1
          && key.getParentColumnsCount() == 1
          && key.getColumns(0) == column
          && key.getParentColumns(0) == parentColumn
          && key.getParentTable().equalsIgnoreCase(parentTable)) {
        return true;
      }
    }
    return false;
  }

  private static void refuseCycle(
      CatalogContext catalog, Schema schema, Table table, List<String> visiting, String path) {
    String name = schema.getName() + "." + table.getName();
    for (String seen : visiting) {
      if (seen.equalsIgnoreCase(name)) {
        List<String> chain = new ArrayList<>(visiting);
        chain.add(name);
        throw new InvalidCatalogException(
            path + ".through",
            "the chain of derived visibility returns to '" + name + "': "
                + String.join(" -> ", chain)
                + ". A row's visibility cannot derive from itself, so a cycle is refused where a"
                + " diamond is fine (docs/design/16-entitlements.md §3.13, D225;"
                + " docs/design/38-existential-visibility.md §3, D265).");
      }
    }
    if (!table.hasEntitlement()
        || (table.getEntitlement().getThroughCount() == 0
            && table.getEntitlement().getInheritedCount() == 0)) {
      return;
    }
    visiting.add(name);
    for (ParentVisibility through : table.getEntitlement().getThroughList()) {
      String parentSchemaName =
          through.getParentSchema().isEmpty() ? schema.getName() : through.getParentSchema();
      Schema parentSchema = schemaNamed(catalog, parentSchemaName);
      Table parent = parentSchema == null ? null : tableNamed(parentSchema, through.getParentTable());
      if (parent != null) {
        refuseCycle(catalog, parentSchema, parent, visiting, path);
      }
    }
    // The other edge of the visibility-dependency graph: a path's target to its endpoint. A bridge
    // is not a node — its own visibility is never consulted, which is what keeps the marketplace
    // shape acyclic where the bridge is itself entitled through this very table (D265 §3).
    for (InheritedVisibility declared : table.getEntitlement().getInheritedList()) {
      String endpointSchemaName =
          declared.getEndpointSchema().isEmpty() ? schema.getName() : declared.getEndpointSchema();
      Schema endpointSchema = schemaNamed(catalog, endpointSchemaName);
      Table endpoint =
          endpointSchema == null ? null : tableNamed(endpointSchema, declared.getEndpointTable());
      if (endpoint != null) {
        refuseCycle(catalog, endpointSchema, endpoint, visiting, path);
      }
    }
    visiting.remove(visiting.size() - 1);
  }

  private static boolean isDeclaredUniqueKey(Table table, int column) {
    for (UniqueKey key : table.getUniqueKeysList()) {
      if (key.getColumnsCount() == 1 && key.getColumns(0) == column) {
        return true;
      }
    }
    return false;
  }

  private static @Nullable Schema schemaNamed(CatalogContext catalog, String name) {
    for (Schema schema : catalog.getSchemasList()) {
      if (schema.getName().equalsIgnoreCase(name)) {
        return schema;
      }
    }
    return null;
  }

  private static @Nullable Table tableNamed(Schema schema, String name) {
    for (Table table : schema.getTablesList()) {
      if (table.getName().equalsIgnoreCase(name)) {
        return table;
      }
    }
    return null;
  }
}
