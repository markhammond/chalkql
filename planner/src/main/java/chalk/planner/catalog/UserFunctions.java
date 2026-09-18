package chalk.planner.catalog;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.FunctionDescriptor;
import chalk.ir.v1.Schema;
import chalk.planner.plan.UserOperators;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.sql.SqlFunctionCategory;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.SqlOperatorTable;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.validate.SqlNameMatcher;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Every function one catalog declares, resolved once with the catalog and shared by every request
 * against it (D77, docs/design/17-user-defined-functions.md §1 and §2).
 *
 * <p>Names are resolved the way a table's are: {@code schema.name} anywhere, and bare in the default
 * schema. A clash with a built-in is refused here rather than shadowed, because a query that means
 * {@code UPPER} and silently gets somebody's {@code upper} is a wrong answer, not a surprise.
 *
 * <p>One instance per {@link RegisteredCatalog}, for the reason {@code SourceConvention} is: the
 * operators are compared by identity in a plan, so two equal-but-distinct copies would be two
 * different functions.
 */
public final class UserFunctions {
  /** A catalog that declares nothing. Cheap, and the common case. */
  public static final UserFunctions EMPTY =
      new UserFunctions(ImmutableList.of(), Map.of(), new IdentityHashMap<>(), Map.of());

  private final List<UserFunction> declarations;
  private final Map<String, SqlOperator> byName;
  private final Map<SqlOperator, UserFunction> declarationOf;
  private final Map<String, UserFunction> byQualifiedName;
  private final SqlOperatorTable operatorTable = new UserOperatorTable();

  private UserFunctions(
      List<UserFunction> declarations,
      Map<String, SqlOperator> byName,
      Map<SqlOperator, UserFunction> declarationOf,
      Map<String, UserFunction> byQualifiedName) {
    this.declarations = declarations;
    this.byName = byName;
    this.declarationOf = declarationOf;
    this.byQualifiedName = byQualifiedName;
  }

  /**
   * Builds the operators for a validated catalog. The descriptor's own shape has already been
   * checked by {@link CatalogValidator}; what is checked here is the one thing that needs Calcite —
   * whether a name is already a built-in.
   */
  public static UserFunctions of(CatalogContext catalog) {
    List<UserFunction> declarations = new ArrayList<>();
    for (Schema schema : catalog.getSchemasList()) {
      for (FunctionDescriptor descriptor : schema.getFunctionsList()) {
        declarations.add(new UserFunction(schema, descriptor));
      }
    }
    if (declarations.isEmpty()) {
      return EMPTY;
    }

    String defaultSchema = catalog.getSchemas(0).getName();
    Map<String, SqlOperator> byName = new LinkedHashMap<>();
    Map<SqlOperator, UserFunction> declarationOf = new IdentityHashMap<>();
    Map<String, UserFunction> byQualifiedName = new LinkedHashMap<>();
    for (UserFunction declaration : declarations) {
      refuseBuiltInClash(declaration.name(), "a built-in", builtIn(declaration.name()));

      SqlOperator operator = UserOperators.create(declaration);
      declarationOf.put(operator, declaration);
      byQualifiedName.put(UserFunction.key(declaration.qualifiedName()), declaration);
      byName.put(UserFunction.key(declaration.qualifiedName()), operator);
      if (declaration.schemaName().equals(defaultSchema)) {
        byName.put(UserFunction.key(declaration.name()), operator);
      }
    }

    return new UserFunctions(
        ImmutableList.copyOf(declarations),
        Map.copyOf(byName),
        declarationOf,
        Map.copyOf(byQualifiedName));
  }

  public boolean isEmpty() {
    return declarations.isEmpty();
  }

  public List<UserFunction> declarations() {
    return declarations;
  }

  /** The operator table to chain after the standard one and the request's libraries. */
  public SqlOperatorTable operatorTable() {
    return operatorTable;
  }

  /** The declaration behind an operator this catalog produced, or null. */
  public @Nullable UserFunction declarationOf(SqlOperator operator) {
    return declarationOf.get(operator);
  }

  /** The declaration named {@code schema.name}, or null. */
  public @Nullable UserFunction byQualifiedName(String qualified) {
    return byQualifiedName.get(UserFunction.key(qualified));
  }

  /**
   * Refuses a clash with the libraries this request asked for (D60). The library table is per
   * request, so this cannot be done at registration; the alternative — letting the built-in win —
   * would silently give a query a function its author did not write.
   */
  public void checkAgainst(List<SqlLibrary> libraries) {
    if (declarations.isEmpty() || libraries.isEmpty()) {
      return;
    }
    SqlOperatorTable table = SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(libraries);
    for (UserFunction declaration : declarations) {
      refuseBuiltInClash(
          declaration.name(),
          "a function of the " + libraries + " library set this request asked for",
          lookup(table, declaration.name()));
    }
  }

  private static void refuseBuiltInClash(String name, String what, boolean clashes) {
    if (clashes) {
      throw new InvalidCatalogException(
          "functions (" + name + ")",
          "'" + name + "' is already " + what + "; a user function may not shadow one, so rename it");
    }
  }

  private static boolean builtIn(String name) {
    return lookup(SqlStdOperatorTable.instance(), name)
        || lookup(chalk.planner.plan.ChalkOperatorTable.chalkOperators(), name);
  }

  private static boolean lookup(SqlOperatorTable table, String name) {
    List<SqlOperator> found = new ArrayList<>();
    SqlIdentifier identifier = new SqlIdentifier(name, org.apache.calcite.sql.parser.SqlParserPos.ZERO);
    for (SqlSyntax syntax : SqlSyntax.values()) {
      table.lookupOperatorOverloads(
          identifier, null, syntax, found, org.apache.calcite.sql.validate.SqlNameMatchers.withCaseSensitive(false));
      if (!found.isEmpty()) {
        return true;
      }
    }
    return false;
  }

  /**
   * The chain link. Calcite hands the whole identifier down, so {@code main.pct_change} and a bare
   * {@code pct_change} in the default schema are both answered from one map.
   */
  private final class UserOperatorTable implements SqlOperatorTable {
    @Override
    public void lookupOperatorOverloads(
        SqlIdentifier opName,
        @Nullable SqlFunctionCategory category,
        SqlSyntax syntax,
        List<SqlOperator> operatorList,
        SqlNameMatcher nameMatcher) {
      if (syntax != SqlSyntax.FUNCTION || opName.names.size() > 2) {
        return;
      }
      SqlOperator operator = byName.get(UserFunction.key(String.join(".", opName.names)));
      if (operator != null && !operatorList.contains(operator)) {
        operatorList.add(operator);
      }
    }

    @Override
    public List<SqlOperator> getOperatorList() {
      return ImmutableList.copyOf(byName.values());
    }
  }
}
