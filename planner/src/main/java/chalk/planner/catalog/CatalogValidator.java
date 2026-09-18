package chalk.planner.catalog;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.ColumnStatistics;
import chalk.ir.v1.CostProfile;
import chalk.ir.v1.ForeignKey;
import chalk.ir.v1.FunctionDescriptor;
import chalk.ir.v1.FunctionKind;
import chalk.ir.v1.Parameter;
import chalk.ir.v1.HistogramBucket;
import chalk.ir.v1.Index;
import chalk.ir.v1.KeyOrder;
import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.IdentifierQuoting;
import chalk.ir.v1.PredicateShape;
import chalk.ir.v1.QueryLanguage;
import chalk.ir.v1.Schema;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.StringCollation;
import chalk.ir.v1.SortDirection;
import chalk.ir.v1.SourceKind;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableCollation;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.planner.plan.SourceDialects;
import chalk.planner.types.ChalkTypeSystem;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

/**
 * The planner's half of catalog validation (docs/design/03-planner.md §3.1). It applies the same
 * rules as the client's {@code Chalk.Catalog.CatalogValidator}, so a descriptor that passes
 * in-process also passes here; the duplication is deliberate — the planner does not trust its
 * caller.
 */
public final class CatalogValidator {
  private CatalogValidator() {}

  public static void validate(CatalogContext catalog) {
    if (catalog.getContextId().isBlank()) {
      throw new InvalidCatalogException("context_id", "the context id is empty");
    }
    if (catalog.getEpoch() < 0) {
      throw new InvalidCatalogException("epoch", "the epoch is " + catalog.getEpoch() + "; it must be >= 0");
    }
    if (catalog.getSchemasCount() == 0) {
      throw new InvalidCatalogException("schemas", "a catalog needs at least one schema");
    }

    Set<String> schemaNames = new HashSet<>();
    for (int s = 0; s < catalog.getSchemasCount(); s++) {
      Schema schema = catalog.getSchemas(s);
      String path = "schemas[" + s + "]";
      if (schema.getName().isBlank()) {
        throw new InvalidCatalogException(path, "the schema name is empty");
      }
      path = path + " (" + schema.getName() + ")";
      if (schema.getSourceId().isBlank()) {
        throw new InvalidCatalogException(path, "the source id is empty");
      }
      if (!schemaNames.add(lower(schema.getName()))) {
        throw new InvalidCatalogException(
            path, "schema name '" + schema.getName() + "' is used twice");
      }
      if (schema.getKind() == SourceKind.SOURCE_KIND_UNSPECIFIED) {
        throw new InvalidCatalogException(path, "the source kind is unspecified");
      }
      if (schema.getKind() == SourceKind.SOURCE_KIND_REMOTE && schema.getDialect().isEmpty()) {
        throw new InvalidCatalogException(
            path, "a remote schema must name the SQL dialect its source speaks");
      }
      validateCostProfile(schema.getCostProfile(), path);
      validateSchema(schema, path);
      for (int t = 0; t < schema.getTablesCount(); t++) {
        validatePartitioning(
            catalog,
            schema.getTables(t),
            path + ".tables[" + t + "] (" + schema.getTables(t).getName() + ")");
      }
    }

    // M5 (D104): the policy travels with the catalog, so a contradictory one is caught here rather
    // than at the first join that reads it.
    validateJoinPolicy(catalog.getJoinPolicy(), "join_policy");
  }

  private static void validateSchema(Schema schema, String path) {
    validateCapabilities(schema, path);
    validateFunctions(schema, path);
    Set<String> tableNames = new HashSet<>();
    for (int t = 0; t < schema.getTablesCount(); t++) {
      Table table = schema.getTables(t);
      String tablePath = path + ".tables[" + t + "]";
      if (table.getName().isBlank()) {
        throw new InvalidCatalogException(tablePath, "the table name is empty");
      }
      tablePath = tablePath + " (" + table.getName() + ")";
      if (!tableNames.add(lower(table.getName()))) {
        throw new InvalidCatalogException(
            tablePath, "table name '" + table.getName() + "' is used twice in this schema");
      }
      validateTable(table, tablePath);
    }

    // Foreign keys name another table, so they can only be checked once every table is known (F14).
    for (int t = 0; t < schema.getTablesCount(); t++) {
      Table table = schema.getTables(t);
      validateForeignKeys(
          schema, table, path + ".tables[" + t + "] (" + table.getName() + ")");
    }
  }

  /**
   * The functions a schema declares (D77, docs/design/17-user-defined-functions.md §1). The bodies
   * themselves are checked where Calcite is available — {@code SqlBodyInliner.check}, called from
   * {@link RegisteredCatalog} — because parsing one needs a parser; everything the descriptor can
   * contradict on its own is checked here, and the client's validator says the same things.
   */
  private static void validateFunctions(Schema schema, String path) {
    Set<String> names = new HashSet<>();
    for (int f = 0; f < schema.getFunctionsCount(); f++) {
      FunctionDescriptor function = schema.getFunctions(f);
      String functionPath = path + ".functions[" + f + "]";
      if (function.getName().isBlank()) {
        throw new InvalidCatalogException(functionPath, "the function name is empty");
      }
      functionPath = functionPath + " (" + function.getName() + ")";
      if (!names.add(lower(function.getName()))) {
        throw new InvalidCatalogException(
            functionPath, "function name '" + function.getName() + "' is used twice in this schema");
      }
      if (function.getKind() == FunctionKind.FUNCTION_KIND_UNSPECIFIED) {
        throw new InvalidCatalogException(functionPath, "the function kind is unspecified");
      }
      if (function.getImplementationCase()
          == FunctionDescriptor.ImplementationCase.IMPLEMENTATION_NOT_SET) {
        throw new InvalidCatalogException(
            functionPath, "the function declares no implementation: set sql, client or native");
      }

      boolean table = function.getKind() == FunctionKind.FUNCTION_KIND_TABLE;
      if (table) {
        if (function.getReturnsTable().getFieldsCount() == 0) {
          throw new InvalidCatalogException(
              functionPath, "a table function must declare the row type it returns");
        }
        if (function.hasReturnType()) {
          throw new InvalidCatalogException(
              functionPath, "a table function returns a table, not a scalar type");
        }
        if (function.getImplementationCase() == FunctionDescriptor.ImplementationCase.NATIVE) {
          throw new InvalidCatalogException(
              functionPath,
              "a native table function has nowhere to be evaluated: a pushed subtree is a query, not"
                  + " a table-valued call (docs/design/17-user-defined-functions.md §8)");
        }
      } else {
        if (!function.hasReturnType()) {
          throw new InvalidCatalogException(functionPath, "the function declares no return type");
        }
        validateType(function.getReturnType(), functionPath + ".return_type");
        if (function.hasReturnsTable()) {
          throw new InvalidCatalogException(
              functionPath, "only a table function declares a returned row type");
        }
        if (function.getRows() != 0) {
          throw new InvalidCatalogException(
              functionPath, "`rows` is a table function's output estimate; this one is not one");
        }
      }

      boolean aggregate = function.getKind() == FunctionKind.FUNCTION_KIND_AGGREGATE;
      if (!aggregate
          && (function.getWindow() || function.getOrdered() || function.getNullTreatment())) {
        throw new InvalidCatalogException(
            functionPath, "`window`, `ordered` and `null_treatment` describe an aggregate");
      }
      if (function.getRows() < 0) {
        throw new InvalidCatalogException(
            functionPath, "`rows` is " + function.getRows() + "; it must be >= 0");
      }
      if (function.getCost() < 0
          || Double.isNaN(function.getCost())
          || Double.isInfinite(function.getCost())) {
        throw new InvalidCatalogException(
            functionPath,
            "`cost` is " + function.getCost() + "; it must be finite and >= 0 (zero means default)");
      }
      if (function.getMonotonicityCount() != 0
          && function.getMonotonicityCount() != function.getParametersCount()) {
        throw new InvalidCatalogException(
            functionPath,
            "monotonicity has "
                + function.getMonotonicityCount()
                + " entries for "
                + function.getParametersCount()
                + " parameters; declare one per parameter or none");
      }
      if (function.getImplementationCase() == FunctionDescriptor.ImplementationCase.SQL
          && function.getSql().getText().isBlank()) {
        throw new InvalidCatalogException(functionPath, "the SQL body is empty");
      }
      if (function.getImplementationCase() == FunctionDescriptor.ImplementationCase.NATIVE
          && schema.getCapabilities().getQueryLanguage() != QueryLanguage.QUERY_LANGUAGE_SQL
          && schema.getCapabilities().getQueryLanguage() != QueryLanguage.QUERY_LANGUAGE_IR) {
        throw new InvalidCatalogException(
            functionPath,
            "a native function is evaluated by its own source, so the schema must take queries;"
                + " this one declares " + schema.getCapabilities().getQueryLanguage());
      }

      Set<String> parameterNames = new HashSet<>();
      for (int p = 0; p < function.getParametersCount(); p++) {
        Parameter parameter = function.getParameters(p);
        String parameterPath = functionPath + ".parameters[" + p + "]";
        if (parameter.getName().isBlank()) {
          throw new InvalidCatalogException(parameterPath, "the parameter name is empty");
        }
        parameterPath = parameterPath + " (" + parameter.getName() + ")";
        if (!parameterNames.add(lower(parameter.getName()))) {
          throw new InvalidCatalogException(
              parameterPath, "parameter name '" + parameter.getName() + "' is used twice");
        }
        validateType(parameter.getType(), parameterPath);
        if (parameter.getType().getKind() == TypeKind.TYPE_KIND_LIST) {
          throw new InvalidCatalogException(
              parameterPath, "a v1 parameter is a scalar; LIST parameters are not supported");
        }
        if (parameter.getOptional() && !parameter.hasDefaultValue()) {
          throw new InvalidCatalogException(
              parameterPath, "an optional parameter must carry the default it stands for");
        }
        if (!parameter.getOptional() && parameter.hasDefaultValue()) {
          throw new InvalidCatalogException(
              parameterPath, "a default is only meaningful on an optional parameter");
        }
        if (parameter.hasDefaultValue()
            && parameter.getDefaultValue().getKindCase() != chalk.ir.v1.Expr.KindCase.LITERAL) {
          throw new InvalidCatalogException(parameterPath, "a parameter default must be a literal");
        }
      }

      for (int c = 0; c < function.getReturnsTable().getFieldsCount(); c++) {
        chalk.ir.v1.Field field = function.getReturnsTable().getFields(c);
        String fieldPath = functionPath + ".returns_table[" + c + "]";
        if (field.getName().isBlank()) {
          throw new InvalidCatalogException(fieldPath, "the column name is empty");
        }
        validateType(field.getType(), fieldPath);
      }
    }
  }

  /**
   * Contradictions between a descriptor and its dialect profile (D82). The client's validator says
   * the same things; this is what protects a planner from a catalog assembled by something other
   * than Chalk.Catalog. A capability model that disagrees with itself would otherwise show up as a
   * wrong answer.
   */
  private static void validateCapabilities(Schema schema, String path) {
    SourceCapabilities capabilities = schema.getCapabilities();
    DialectProfile profile = schema.getDialectProfile();

    // D256: a name that is neither a tuned preset, ansi, nor a DatabaseProduct (aliases included)
    // is refused here rather than silently planned as ANSI. Empty is untouched — D249 already
    // reads it as ANSI, and this decision does not change that.
    if (!profile.getDialect().isEmpty() && !SourceDialects.isAccepted(profile.getDialect())) {
      throw new InvalidCatalogException(
          path,
          "dialect_profile.dialect '"
              + profile.getDialect()
              + "' is not one of the presets this planner accepts: "
              + String.join(", ", SourceDialects.presetNames())
              + ". Leave it empty for ANSI.");
    }

    if (capabilities.getQueryLanguage() == QueryLanguage.QUERY_LANGUAGE_NONE && pushes(capabilities)) {
      throw new InvalidCatalogException(
          path,
          "the source declares pushable work but QUERY_LANGUAGE_NONE, so it can only be scanned and "
              + "nothing would ever be pushed");
    }
    if (capabilities.getSupportsOffset() && !capabilities.getSupportsLimit()) {
      throw new InvalidCatalogException(
          path,
          "supports_offset without supports_limit: an OFFSET is pushed as part of a fetch, so a "
              + "source that takes one must take a LIMIT too");
    }
    if (capabilities.getSupportsHaving() && !capabilities.getSupportsGroupBy()) {
      throw new InvalidCatalogException(
          path, "supports_having without supports_group_by: a HAVING has nothing to filter");
    }
    if (capabilities.getMaxInList() > 0
        && !capabilities.getPushablePredicatesList().contains(PredicateShape.PREDICATE_SHAPE_IN)) {
      throw new InvalidCatalogException(
          path,
          "max_in_list is "
              + capabilities.getMaxInList()
              + " but PREDICATE_SHAPE_IN is not pushable, so no IN list is ever pushed");
    }
    if (capabilities.getMaxPushdownRows() < 0) {
      throw new InvalidCatalogException(
          path,
          "max_pushdown_rows is "
              + capabilities.getMaxPushdownRows()
              + "; it must be zero (unlimited) or positive");
    }
    if (profile.getStringCollation() == StringCollation.STRING_COLLATION_CASE_INSENSITIVE
        || profile.getStringCollation() == StringCollation.STRING_COLLATION_LOCALE) {
      for (PredicateShape shape : capabilities.getPushablePredicatesList()) {
        if (shape == PredicateShape.PREDICATE_SHAPE_LIKE
            || shape == PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX) {
          throw new InvalidCatalogException(
              path,
              shape
                  + " is declared pushable but the dialect profile says "
                  + profile.getStringCollation()
                  + "; a LIKE evaluated under another collation can match different rows (D89)");
        }
      }
    }
    if (capabilities.getQueryLanguage() == QueryLanguage.QUERY_LANGUAGE_SQL
        && profile.getQuoting() == IdentifierQuoting.IDENTIFIER_QUOTING_UNSPECIFIED) {
      throw new InvalidCatalogException(
          path, "a SQL source must say how it quotes identifiers (dialect_profile.quoting)");
    }

    // M5: a broadcast ships rows into the source's own query, so a source that cannot take an IN
    // list of literals certainly cannot take a VALUES relation of them.
    if (capabilities.getSupportsValuesJoin() && capabilities.getMaxInList() <= 0) {
      throw new InvalidCatalogException(
          path,
          "supports_values_join with max_in_list 0: a broadcast join ships the small side's rows "
              + "into the source's query, so a source that accepts no list of values cannot do one");
    }

    // F50: a row-constructor IN list is an IN list, and the same ceiling sizes it — in key rows
    // rather than in values.
    if (capabilities.getSupportsRowValueInList() && capabilities.getMaxInList() <= 0) {
      throw new InvalidCatalogException(
          path,
          "supports_row_value_in_list with max_in_list 0: a row-constructor IN list is still an IN "
              + "list, and max_in_list is what sizes a call of them");
    }
  }

  /**
   * A partitioned table is a claim about where rows live (D106): every partition must name a table
   * this catalog has, the partition column must exist, and a value partition must carry a literal.
   */
  private static void validatePartitioning(
      chalk.ir.v1.CatalogContext catalog, Table table, String path) {
    if (!table.hasPartitioning()) {
      return;
    }

    chalk.ir.v1.Partitioning partitioning = table.getPartitioning();
    if (partitioning.getPartitionsCount() == 0) {
      throw new InvalidCatalogException(path, "a partitioned table declares no partitions");
    }
    if (partitioning.getPartitionColumn() >= table.getColumnsCount()) {
      throw new InvalidCatalogException(
          path,
          "partition_column is "
              + partitioning.getPartitionColumn()
              + " but the table has "
              + table.getColumnsCount()
              + " columns");
    }

    for (chalk.ir.v1.Partition partition : partitioning.getPartitionsList()) {
      if (partition.getSourceId().isEmpty() || partition.getTable().isEmpty()) {
        throw new InvalidCatalogException(
            path, "every partition must name a source and a table");
      }
      if (partition.getMatchCase() == chalk.ir.v1.Partition.MatchCase.MATCH_NOT_SET) {
        throw new InvalidCatalogException(
            path,
            "partition '"
                + partition.getSourceId()
                + "."
                + partition.getTable()
                + "' says neither which value nor which range it holds");
      }
      if (partition.hasValue()
          && partition.getValue().getKindCase() != chalk.ir.v1.Expr.KindCase.LITERAL) {
        throw new InvalidCatalogException(
            path,
            "partition '"
                + partition.getSourceId()
                + "."
                + partition.getTable()
                + "' has a value that is not a literal");
      }

      chalk.ir.v1.Schema owner = null;
      for (chalk.ir.v1.Schema schema : catalog.getSchemasList()) {
        if (schema.getName().equals(partition.getSourceId())) {
          owner = schema;
          break;
        }
      }
      if (owner == null) {
        throw new InvalidCatalogException(
            path, "partition schema '" + partition.getSourceId() + "' is not in this catalog");
      }
      boolean found = false;
      for (Table candidate : owner.getTablesList()) {
        if (candidate.getName().equals(partition.getTable())) {
          found = true;
          if (candidate.getColumnsCount() != table.getColumnsCount()) {
            throw new InvalidCatalogException(
                path,
                "partition '"
                    + partition.getSourceId()
                    + "."
                    + partition.getTable()
                    + "' has "
                    + candidate.getColumnsCount()
                    + " columns and the partitioned table has "
                    + table.getColumnsCount()
                    + "; every partition must have the logical table's row type");
          }
          break;
        }
      }
      if (!found) {
        throw new InvalidCatalogException(
            path,
            "partition '"
                + partition.getSourceId()
                + "."
                + partition.getTable()
                + "' names a table schema '"
                + partition.getSourceId()
                + "' does not have");
      }
    }
  }

  /** The cross-source join policy's limits (D104): every one is zero (inherit) or positive. */
  private static void validateJoinPolicy(chalk.ir.v1.CrossSourceJoinPolicy policy, String path) {
    if (policy.getBroadcastMaxRows() < 0
        || policy.getLookupMaxCalls() < 0
        || policy.getLocalJoinMaxRows() < 0
        || policy.getUnknownRowCountAssumption() < 0) {
      throw new InvalidCatalogException(
          path,
          "every join-policy limit must be zero (the planner's default) or positive; "
              + "broadcast_max_rows="
              + policy.getBroadcastMaxRows()
              + ", lookup_max_calls="
              + policy.getLookupMaxCalls()
              + ", local_join_max_rows="
              + policy.getLocalJoinMaxRows()
              + ", unknown_row_count_assumption="
              + policy.getUnknownRowCountAssumption());
    }
    for (chalk.ir.v1.SourcePairRule rule : policy.getPairsList()) {
      if (rule.getAllowedCount() > 0
          && rule.getPreferred() != chalk.ir.v1.JoinStrategy.JOIN_STRATEGY_UNSPECIFIED
          && !rule.getAllowedList().contains(rule.getPreferred())) {
        throw new InvalidCatalogException(
            path,
            "pair rule '"
                + rule.getLeftSource()
                + "' -> '"
                + rule.getRightSource()
                + "' prefers "
                + rule.getPreferred()
                + ", which is not in its allowed list");
      }
    }
  }

  /** Whether a descriptor claims any work at all beyond a plain scan. */
  private static boolean pushes(SourceCapabilities c) {
    return c.getPushablePredicatesCount() > 0
        || c.getPushableFunctionsCount() > 0
        || c.getPushableAggregatesCount() > 0
        || c.getSupportsProject()
        || c.getSupportsSort()
        || c.getSupportsLimit()
        || c.getSupportsOffset()
        || c.getSupportsDistinct()
        || c.getSupportsGroupBy()
        || c.getSupportsHaving()
        || c.getSupportsInnerJoin()
        || c.getSupportsOuterJoin()
        || c.getSupportsSemiAntiJoin();
  }

  /**
   * A foreign key is a claim the cost model acts on (F14): the child's columns exist, the parent
   * exists in the same schema, the two key arities match, and each pair has the same type kind.
   */
  private static void validateForeignKeys(Schema schema, Table table, String path) {
    Set<String> names = new HashSet<>();
    for (int k = 0; k < table.getForeignKeysCount(); k++) {
      ForeignKey key = table.getForeignKeys(k);
      String keyPath = path + ".foreign_keys[" + k + "]";
      if (!key.getName().isBlank() && !names.add(lower(key.getName()))) {
        throw new InvalidCatalogException(
            keyPath, "foreign key name '" + key.getName() + "' is used twice on this table");
      }
      if (key.getColumnsCount() == 0) {
        throw new InvalidCatalogException(keyPath, "a foreign key has no columns");
      }
      if (key.getColumnsCount() != key.getParentColumnsCount()) {
        throw new InvalidCatalogException(
            keyPath,
            "the key has "
                + key.getColumnsCount()
                + " column(s) and the parent key has "
                + key.getParentColumnsCount()
                + "; a foreign key pairs them one for one");
      }

      Table parent = null;
      for (Table candidate : schema.getTablesList()) {
        if (lower(candidate.getName()).equals(lower(key.getParentTable()))) {
          parent = candidate;
          break;
        }
      }
      if (parent == null) {
        throw new InvalidCatalogException(
            keyPath,
            "the parent table '" + key.getParentTable() + "' is not in schema '"
                + schema.getName() + "'");
      }

      Set<Integer> seen = new HashSet<>();
      for (int c = 0; c < key.getColumnsCount(); c++) {
        int child = key.getColumns(c);
        int target = key.getParentColumns(c);
        requireColumn(child, table.getColumnsCount(), keyPath);
        if (!seen.add(child)) {
          throw new InvalidCatalogException(
              keyPath, "column " + child + " appears twice in the same foreign key");
        }
        requireColumn(target, parent.getColumnsCount(), keyPath + " (parent '" + parent.getName() + "')");

        TypeKind childKind = table.getColumns(child).getType().getKind();
        TypeKind parentKind = parent.getColumns(target).getType().getKind();
        if (childKind != parentKind) {
          throw new InvalidCatalogException(
              keyPath,
              "column '"
                  + table.getColumns(child).getName()
                  + "' is "
                  + childKind
                  + " but '"
                  + parent.getName()
                  + "."
                  + parent.getColumns(target).getName()
                  + "' is "
                  + parentKind
                  + "; a foreign key's columns must have the same kind");
        }
      }
    }
  }

  private static void validateTable(Table table, String path) {
    if (table.getColumnsCount() == 0) {
      throw new InvalidCatalogException(path, "a table needs at least one column");
    }
    if (table.getRowCount() < -1) {
      throw new InvalidCatalogException(
          path, "row_count is " + table.getRowCount() + "; it must be >= -1 (-1 means unknown)");
    }

    Set<String> columnNames = new HashSet<>();
    for (int c = 0; c < table.getColumnsCount(); c++) {
      Column column = table.getColumns(c);
      String columnPath = path + ".columns[" + c + "]";
      if (column.getName().isBlank()) {
        throw new InvalidCatalogException(columnPath, "the column name is empty");
      }
      if (!columnNames.add(lower(column.getName()))) {
        throw new InvalidCatalogException(
            columnPath, "column name '" + column.getName() + "' is used twice in this table");
      }
      validateType(column.getType(), columnPath + " (" + column.getName() + ")");
    }

    int columns = table.getColumnsCount();
    for (int k = 0; k < table.getUniqueKeysCount(); k++) {
      UniqueKey key = table.getUniqueKeys(k);
      String keyPath = path + ".unique_keys[" + k + "]";
      if (key.getColumnsCount() == 0) {
        throw new InvalidCatalogException(keyPath, "a unique key has no columns");
      }
      Set<Integer> seen = new HashSet<>();
      for (int column : key.getColumnsList()) {
        requireColumn(column, columns, keyPath);
        if (!seen.add(column)) {
          throw new InvalidCatalogException(
              keyPath, "column " + column + " appears twice in the same key");
        }
      }
    }

    for (int i = 0; i < table.getCollationsCount(); i++) {
      TableCollation collation = table.getCollations(i);
      String collationPath = path + ".collations[" + i + "]";
      if (collation.getKeysCount() == 0) {
        throw new InvalidCatalogException(collationPath, "a collation has no keys");
      }
      Set<Integer> seen = new HashSet<>();
      for (int j = 0; j < collation.getKeysCount(); j++) {
        KeyOrder key = collation.getKeys(j);
        String keyPath = collationPath + ".keys[" + j + "]";
        requireColumn(key.getColumn(), columns, keyPath);
        if (key.getDirection() == SortDirection.SORT_DIRECTION_UNSPECIFIED) {
          throw new InvalidCatalogException(
              keyPath,
              "the sort direction is unspecified; Calcite compares collations including null "
                  + "direction, so an unspecified one never satisfies an ORDER BY");
        }
        if (!seen.add(key.getColumn())) {
          throw new InvalidCatalogException(
              keyPath, "column " + key.getColumn() + " appears twice in the same collation");
        }
      }
    }

    Set<String> indexNames = new HashSet<>();
    for (int i = 0; i < table.getIndexesCount(); i++) {
      Index index = table.getIndexes(i);
      String indexPath = path + ".indexes[" + i + "]";
      if (index.getName().isBlank()) {
        throw new InvalidCatalogException(indexPath, "the index name is empty");
      }
      if (!indexNames.add(lower(index.getName()))) {
        throw new InvalidCatalogException(
            indexPath, "index name '" + index.getName() + "' is used twice on this table");
      }
      if (index.getKind() == chalk.ir.v1.IndexKind.INDEX_KIND_UNSPECIFIED) {
        throw new InvalidCatalogException(indexPath, "the index kind is unspecified");
      }
      if (index.getColumnsCount() == 0) {
        throw new InvalidCatalogException(indexPath, "an index has no columns");
      }
      Set<Integer> indexColumns = new HashSet<>();
      for (int column : index.getColumnsList()) {
        requireColumn(column, columns, indexPath);
        if (!indexColumns.add(column)) {
          throw new InvalidCatalogException(
              indexPath, "column " + column + " appears twice in the same index key");
        }
      }
      if (index.getDirectionsCount() != 0
          && index.getDirectionsCount() != index.getColumnsCount()) {
        throw new InvalidCatalogException(
            indexPath,
            "the index declares "
                + index.getDirectionsCount()
                + " key direction(s) for "
                + index.getColumnsCount()
                + " key column(s); declare one per column or none at all");
      }
      for (int d = 0; d < index.getDirectionsCount(); d++) {
        if (index.getDirections(d) == SortDirection.SORT_DIRECTION_UNSPECIFIED) {
          throw new InvalidCatalogException(
              indexPath + ".directions[" + d + "]",
              "the sort direction is unspecified; leave the directions empty for the "
                  + "ascending-nulls-last default rather than declaring an unspecified one");
        }
      }
      validateCovering(table, index, indexPath, indexColumns, columns);
    }

    validateCostProfile(table.getCostProfile(), path);

    for (int c = 0; c < table.getColumnsCount(); c++) {
      validateStatistics(
          table.getColumns(c).getStatistics(),
          path + ".columns[" + c + "] (" + table.getColumns(c).getName() + ")");
    }
  }

  /**
   * Statistics are claims the cost model acts on, so the shape is checked; the values are the
   * source's word and are never second-guessed. -1 is the one legitimate "unknown" (D36).
   */
  private static void validateStatistics(ColumnStatistics statistics, String path) {
    if (statistics.getDistinctCount() < -1) {
      throw new InvalidCatalogException(
          path,
          "distinct_count is " + statistics.getDistinctCount() + "; it must be >= -1 (-1 means unknown)");
    }
    if (statistics.getNullCount() < -1) {
      throw new InvalidCatalogException(
          path, "null_count is " + statistics.getNullCount() + "; it must be >= -1 (-1 means unknown)");
    }
    for (int b = 0; b < statistics.getHistogram().getBucketsCount(); b++) {
      HistogramBucket bucket = statistics.getHistogram().getBuckets(b);
      if (bucket.getCount() < 0) {
        throw new InvalidCatalogException(
            path + ".histogram[" + b + "]",
            "the bucket count is " + bucket.getCount() + "; it must be >= 0");
      }
      if (bucket.getDistinctCount() < -1) {
        throw new InvalidCatalogException(
            path + ".histogram[" + b + "]",
            "distinct_count is " + bucket.getDistinctCount() + "; it must be >= -1");
      }
    }
    for (int v = 0; v < statistics.getFrequentValuesCount(); v++) {
      if (statistics.getFrequentValues(v).getCount() < 0) {
        throw new InvalidCatalogException(
            path + ".frequent_values[" + v + "]",
            "the value count is " + statistics.getFrequentValues(v).getCount() + "; it must be >= 0");
      }
    }
  }

  /**
   * A cost may not be negative, and may not be infinite or NaN: Volcano compares costs and a NaN
   * makes every comparison false, which is a plan chosen by accident (D38).
   */
  private static void validateCostProfile(CostProfile profile, String path) {
    requireCost(profile.getScanRowCost(), "scan_row_cost", path);
    requireCost(profile.getLookupSeekCost(), "lookup_seek_cost", path);
    requireCost(profile.getLookupRowCost(), "lookup_row_cost", path);
    requireCost(profile.getRemoteCallCost(), "remote_call_cost", path);
    requireCost(profile.getRemoteRowCost(), "remote_row_cost", path);
  }

  private static void requireCost(double cost, String name, String path) {
    if (cost < 0 || Double.isNaN(cost) || Double.isInfinite(cost)) {
      throw new InvalidCatalogException(
          path + ".cost_profile",
          name + " is " + cost + "; a cost must be finite and >= 0 (zero means 'inherit')");
    }
  }

  /**
   * The covering set of a clustered index (D257, {@code docs/design/34-clustered-indexes.md} §2):
   * real columns, no duplicates, ascending, and always a superset of the key, because a range seek
   * binary-searches the key columns in the copy. Empty means every column. Only a clustered index
   * has a copy to describe, so any other kind declaring one is a registration error rather than a
   * field the planner would silently ignore. Mirrors {@code Chalk.Catalog.CatalogValidator}.
   */
  private static void validateCovering(
      Table table, Index index, String indexPath, Set<Integer> keyColumns, int columns) {
    if (index.getCoveringCount() == 0) {
      return;
    }

    if (index.getKind() != chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED) {
      throw new InvalidCatalogException(
          indexPath,
          "index '"
              + index.getName()
              + "' is "
              + index.getKind()
              + " and declares a covering set, but only a CLUSTERED index holds a copy of its"
              + " columns. Declare the index clustered, or drop the covering set.");
    }

    Set<Integer> seen = new HashSet<>();
    int previous = -1;
    for (int c = 0; c < index.getCoveringCount(); c++) {
      int column = index.getCovering(c);
      requireColumn(column, columns, indexPath + ".covering[" + c + "]");
      if (!seen.add(column)) {
        throw new InvalidCatalogException(
            indexPath + ".covering[" + c + "]",
            "column " + column + " appears twice in the covering set of index '"
                + index.getName() + "'");
      }
      if (column <= previous) {
        throw new InvalidCatalogException(
            indexPath + ".covering[" + c + "]",
            "the covering set of index '"
                + index.getName()
                + "' is not ascending: column "
                + column
                + " follows column "
                + previous);
      }
      previous = column;
    }

    for (int key : keyColumns) {
      if (!seen.contains(key)) {
        throw new InvalidCatalogException(
            indexPath,
            "the covering set of index '"
                + index.getName()
                + "' leaves out key column "
                + key
                + " ('"
                + table.getColumns(key).getName()
                + "'). A range seek binary-searches the key in the copy, so the key columns are"
                + " always covered.");
      }
    }
  }

  private static void requireColumn(int column, int columnCount, String path) {
    if (column < 0 || column >= columnCount) {
      throw new InvalidCatalogException(
          path,
          "column index " + column + " is out of range for a table of " + columnCount + " columns");
    }
  }

  private static void validateType(Type type, String path) {
    if (type.getKind() == TypeKind.TYPE_KIND_UNSPECIFIED) {
      throw new InvalidCatalogException(path, "the column type is unspecified");
    }
    switch (type.getKind()) {
      case TYPE_KIND_DECIMAL -> {
        if (type.getPrecision() < 1
            || type.getPrecision() > ChalkTypeSystem.MAX_DECIMAL_PRECISION) {
          throw new InvalidCatalogException(
              path,
              "DECIMAL precision is "
                  + type.getPrecision()
                  + "; it must be 1.."
                  + ChalkTypeSystem.MAX_DECIMAL_PRECISION);
        }
        if (type.getScale() > type.getPrecision()) {
          throw new InvalidCatalogException(
              path, "DECIMAL scale " + type.getScale() + " exceeds precision " + type.getPrecision());
        }
      }
      case TYPE_KIND_TIME, TYPE_KIND_TIMESTAMP, TYPE_KIND_TIMESTAMP_TZ -> {
        int max =
            type.getKind() == TypeKind.TYPE_KIND_TIME
                ? ChalkTypeSystem.MAX_TIME_PRECISION
                : ChalkTypeSystem.MAX_TIMESTAMP_PRECISION;
        if (type.getPrecision() > max) {
          throw new InvalidCatalogException(
              path, type.getKind() + " precision is " + type.getPrecision() + "; the maximum is " + max);
        }
        if (type.getScale() != 0) {
          throw new InvalidCatalogException(path, type.getKind() + " carries a scale; scale is DECIMAL-only");
        }
      }
      default -> {
        if (type.getPrecision() != 0 || type.getScale() != 0) {
          throw new InvalidCatalogException(
              path,
              type.getKind()
                  + " carries precision "
                  + type.getPrecision()
                  + " / scale "
                  + type.getScale()
                  + "; both must be zero");
        }
      }
    }
  }

  private static String lower(String value) {
    return value.toLowerCase(Locale.ROOT);
  }
}
