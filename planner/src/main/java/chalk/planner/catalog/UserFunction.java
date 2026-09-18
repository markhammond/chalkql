package chalk.planner.catalog;

import chalk.ir.v1.FunctionDescriptor;
import chalk.ir.v1.FunctionKind;
import chalk.ir.v1.Monotonicity;
import chalk.ir.v1.Parameter;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Volatility;
import java.util.List;
import java.util.Locale;

/**
 * One function the catalog declares, together with the schema that declares it
 * (docs/design/17-user-defined-functions.md §1, D77).
 *
 * <p>A function is addressed exactly as a table is: {@code schema.name}, or bare in the default
 * schema. The qualified name is what travels in the IR and what the plan digest sees, so it is
 * computed once here rather than spelled out at every use.
 */
public final class UserFunction {
  private final Schema schema;
  private final FunctionDescriptor descriptor;
  private final String qualifiedName;

  UserFunction(Schema schema, FunctionDescriptor descriptor) {
    this.schema = schema;
    this.descriptor = descriptor;
    this.qualifiedName = schema.getName() + "." + descriptor.getName();
  }

  public Schema schema() {
    return schema;
  }

  public String schemaName() {
    return schema.getName();
  }

  public String name() {
    return descriptor.getName();
  }

  /** {@code schema.name} — the spelling {@code ScalarCall.user_function} carries. */
  public String qualifiedName() {
    return qualifiedName;
  }

  public FunctionDescriptor descriptor() {
    return descriptor;
  }

  public FunctionKind kind() {
    return descriptor.getKind();
  }

  public Volatility volatility() {
    return descriptor.getVolatility() == Volatility.VOLATILITY_UNSPECIFIED
        ? Volatility.VOLATILITY_IMMUTABLE
        : descriptor.getVolatility();
  }

  public boolean strict() {
    return descriptor.getStrict();
  }

  public List<Parameter> parameters() {
    return descriptor.getParametersList();
  }

  public boolean isSqlBodied() {
    return descriptor.getImplementationCase() == FunctionDescriptor.ImplementationCase.SQL;
  }

  public boolean isClientBodied() {
    return descriptor.getImplementationCase() == FunctionDescriptor.ImplementationCase.CLIENT;
  }

  public boolean isNative() {
    return descriptor.getImplementationCase() == FunctionDescriptor.ImplementationCase.NATIVE;
  }

  /** The SQL text of a SQL body. Empty for the other kinds. */
  public String sqlText() {
    return descriptor.getSql().getText();
  }

  /** How the declaring source spells this function; its own name when the descriptor says nothing. */
  public String dialectName() {
    String spelling = descriptor.getNative().getDialectName();
    return spelling.isEmpty() ? descriptor.getName() : spelling;
  }

  /** The monotonicity declared for parameter {@code index}, or NONE. */
  public Monotonicity monotonicityOf(int index) {
    if (index < 0 || index >= descriptor.getMonotonicityCount()) {
      return Monotonicity.MONOTONICITY_NONE;
    }
    Monotonicity declared = descriptor.getMonotonicity(index);
    return declared == Monotonicity.MONOTONICITY_UNSPECIFIED
        ? Monotonicity.MONOTONICITY_NONE
        : declared;
  }

  /** Whether any parameter carries a monotonicity worth propagating. */
  public boolean isMonotone() {
    for (int i = 0; i < descriptor.getMonotonicityCount(); i++) {
      Monotonicity declared = descriptor.getMonotonicity(i);
      if (declared != Monotonicity.MONOTONICITY_UNSPECIFIED
          && declared != Monotonicity.MONOTONICITY_NONE) {
        return true;
      }
    }
    return false;
  }

  /** The lower-cased key names are matched by, since {@code Lex.MYSQL_ANSI} matches case-blind. */
  static String key(String name) {
    return name.toLowerCase(Locale.ROOT);
  }

  @Override
  public String toString() {
    return qualifiedName;
  }
}
