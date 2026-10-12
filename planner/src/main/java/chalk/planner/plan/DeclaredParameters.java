package chalk.planner.plan;

import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.ErrorCode;
import chalk.planner.InvalidArgumentException;
import chalk.planner.types.TypeMapper;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Set;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlDynamicParam;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlOrderBy;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The types a host declared for a statement's parameters, held when the statement is validated
 * (D337). Calcite types a parameter from what is around it, so {@code ts >= ? - INTERVAL '60'
 * SECOND} gives it the interval's type, whatever was declared. The validator gives a declared
 * parameter its declared type instead ({@link ChalkPlanner#declareParameterTypes}), and the plan
 * carries the parameter as it is, with no cast around it.
 *
 * <p>Two kinds keep the type Calcite infers. A row count in {@code LIMIT}, {@code OFFSET} or {@code
 * FETCH} must be declared an integer type, and a parameter declared a LIST keeps the list handling
 * it has today.
 */
final class DeclaredParameters {
  private DeclaredParameters() {}

  /**
   * The type each of {@code statement}'s parameters takes at validation, by ordinal: its declared
   * type, or null where it is inferred. Empty when nothing was declared. Refuses a declaration that
   * does not have one type per parameter, and a row count declared other than an integer.
   */
  static List<@Nullable RelDataType> of(SqlNode statement, List<Type> declared, TypeMapper types) {
    if (declared.isEmpty()) {
      return List.of();
    }

    int count = count(statement);
    if (count != declared.size()) {
      throw new InvalidArgumentException(
          ErrorCode.PARAMETER_BINDING,
          "The statement has "
              + count
              + (count == 1 ? " parameter" : " parameters")
              + ", and "
              + declared.size()
              + (declared.size() == 1 ? " type was" : " types were")
              + " declared for them. Declare one type for each parameter, in order, or none.");
    }

    Set<SqlNode> bounds = rowBounds(statement);
    for (SqlNode bound : bounds) {
      int index = ((SqlDynamicParam) bound).getIndex();
      Type type = declared.get(index);
      if (!isInteger(type.getKind())) {
        throw new InvalidArgumentException(
            ErrorCode.PARAMETER_BINDING,
            "Parameter "
                + (index + 1)
                + " is a row count in LIMIT, OFFSET or FETCH, so it must be declared an integer"
                + " type, and it was declared "
                + type.getKind().name().substring("TYPE_KIND_".length())
                + ".");
      }
    }

    Set<Integer> inferred = new java.util.HashSet<>();
    for (SqlNode bound : bounds) {
      inferred.add(((SqlDynamicParam) bound).getIndex());
    }
    List<@Nullable RelDataType> result = new java.util.ArrayList<>(declared.size());
    for (int i = 0; i < declared.size(); i++) {
      Type type = declared.get(i);
      result.add(
          inferred.contains(i) || type.getKind() == TypeKind.TYPE_KIND_LIST
              ? null
              : types.toCalcite(type));
    }
    return result;
  }

  /** How many parameters the statement has: one more than the highest ordinal. */
  private static int count(SqlNode statement) {
    int[] highest = {-1};
    statement.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public @Nullable Void visit(SqlDynamicParam param) {
            highest[0] = Math.max(highest[0], param.getIndex());
            return null;
          }
        });
    return highest[0] + 1;
  }

  /** The parameters that stand alone as a LIMIT, OFFSET or FETCH, by identity. */
  private static Set<SqlNode> rowBounds(SqlNode statement) {
    Set<SqlNode> bounds = Collections.newSetFromMap(new IdentityHashMap<>());
    statement.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public @Nullable Void visit(SqlCall call) {
            if (call instanceof SqlSelect select) {
              add(select.getOffset());
              add(select.getFetch());
            } else if (call instanceof SqlOrderBy orderBy) {
              add(orderBy.offset);
              add(orderBy.fetch);
            }

            return super.visit(call);
          }

          private void add(@Nullable SqlNode bound) {
            if (bound instanceof SqlDynamicParam) {
              bounds.add(bound);
            }
          }
        });
    return bounds;
  }

  private static boolean isInteger(TypeKind kind) {
    return switch (kind) {
      case TYPE_KIND_I8, TYPE_KIND_I16, TYPE_KIND_I32, TYPE_KIND_I64 -> true;
      default -> false;
    };
  }
}
