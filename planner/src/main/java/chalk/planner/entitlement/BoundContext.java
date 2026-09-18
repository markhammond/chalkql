package chalk.planner.entitlement;

import chalk.ir.v1.Expr;
import chalk.ir.v1.Literal;
import chalk.ir.v1.RowType;
import chalk.ir.v1.VirtualRow;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import com.google.common.collect.ImmutableMap;
import com.google.common.collect.ImmutableSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

/**
 * One request's execution context, validated at bind (docs/design/16-entitlements.md §2).
 *
 * <p>Three kinds of thing, bound by name and referred to from a descriptor's SQL as
 * {@code @ctx.<name>}: a <b>scalar</b>, a typed value; a <b>list</b>, a relation of one or more
 * columns used only inside an {@code IN} list and {@code NOT NULL} in every column; and a
 * <b>relation</b>, a table in a {@code FROM}. The kind is declared rather than inferred, because the
 * same rows read as a membership set and as a table are different things to the planner, and telling
 * a host "a list belongs in an IN list" is only possible if the planner knows which one it was
 * handed.
 *
 * <p>Nothing here is mutable and nothing is shared between requests. The values never reach the
 * statistics, the audit event or an error message; under prepare-time binding the <em>folded</em>
 * ones do reach the plan and the digest, which is what makes two role sets two plans (D152).
 */
public final class BoundContext {
  /** How many rows a list may have and still be folded into a literal {@code IN} list. */
  public static final int DEFAULT_FOLD_MAX_ROWS = 64;

  /**
   * The per-request schema the context's relations and unfolded lists live in — a reserved name, so
   * that a host schema called {@code ctx} cannot be what the fold resolves against (D223).
   */
  public static final String SCHEMA = chalk.planner.ReservedNames.CTX;

  /** No context at all: what every request without entitlements binds, and it costs nothing. */
  public static final BoundContext EMPTY =
      new BoundContext(
          ImmutableMap.of(), ImmutableMap.of(), DEFAULT_FOLD_MAX_ROWS, false, ImmutableSet.of());

  private final ImmutableMap<String, Expr> scalars;
  private final ImmutableMap<String, Relation> relations;
  private final int foldMaxRows;
  private final boolean shapeOnly;

  /**
   * The names bound as <em>shapes</em> rather than as values (D232). Every name under a
   * message-wide {@code shape_only}, the ones that said so themselves otherwise, and none at all for
   * the prepare-time binding that is the primary mode.
   */
  private final ImmutableSet<String> shapes;

  /** One bound relation: a list or a table, its declared row type and its rows. */
  public record Relation(String name, ContextRelationKind kind, RowType rowType, java.util.List<VirtualRow> rows) {
    public boolean isList() {
      return kind != ContextRelationKind.CONTEXT_RELATION_KIND_RELATION;
    }

    public int columnCount() {
      return rowType.getFieldsCount();
    }
  }

  private BoundContext(
      ImmutableMap<String, Expr> scalars,
      ImmutableMap<String, Relation> relations,
      int foldMaxRows,
      boolean shapeOnly,
      ImmutableSet<String> shapes) {
    this.scalars = scalars;
    this.relations = relations;
    this.foldMaxRows = foldMaxRows;
    this.shapeOnly = shapeOnly;
    this.shapes = shapes;
  }

  /**
   * The context a request bound, checked for the things a planner can check without a query: one
   * binding per name, a scalar that is a literal, a row that has the arity its row type declares,
   * and — for a list — no NULL member.
   *
   * @throws IllegalArgumentException naming the binding, which becomes {@code INVALID_ARGUMENT}
   */
  public static BoundContext of(RequestContext proto) {
    boolean shapeOnly = proto.getShapeOnly();
    if (proto.getScalarsCount() == 0 && proto.getRelationsCount() == 0) {
      return proto.getFoldMaxRows() == 0 && !shapeOnly
          ? EMPTY
          : new BoundContext(
              ImmutableMap.of(),
              ImmutableMap.of(),
              ceiling(proto.getFoldMaxRows()),
              shapeOnly,
              ImmutableSet.of());
    }

    Map<String, Expr> scalars = new LinkedHashMap<>();
    Map<String, Relation> relations = new LinkedHashMap<>();
    Set<String> shapes = new LinkedHashSet<>();

    for (ContextScalar scalar : proto.getScalarsList()) {
      String name = require(scalar.getName(), "a context scalar");
      boolean shape = shapeOnly || scalar.getShape();
      if (shape) {
        // A shape declares the type and nothing else (D209, D232): the planner has to type the
        // parameter it plans in the value's place, and a value would defeat the whole point of it.
        if (!scalar.getValue().hasType()) {
          throw new IllegalArgumentException(
              "context scalar '" + name + "' declares no type. A shape binds a name, a kind and a "
                  + "type and no value (docs/design/16-entitlements.md §2, §2.1, D209, D232).");
        }
        if (scalar.getValue().getKindCase() != Expr.KindCase.KIND_NOT_SET) {
          throw new IllegalArgumentException(
              "context scalar '" + name + "' carries a value and says it is a shape. Bind the "
                  + "value at execution, which is what a shape exists for "
                  + "(docs/design/16-entitlements.md §2, §2.1, D209, D232).");
        }
        shapes.add(name);
      } else if (scalar.getValue().getKindCase() != Expr.KindCase.LITERAL) {
        throw new IllegalArgumentException(
            "context scalar '" + name + "' is not a literal. A context binds values, not "
                + "expressions: a predicate the planner cannot see cannot be pushed, costed or "
                + "audited, so a host that needs code runs it before the statement and binds the "
                + "result (docs/design/16-entitlements.md §2).");
      }
      if (scalars.put(name, scalar.getValue()) != null || relations.containsKey(name)) {
        throw new IllegalArgumentException("context name '" + name + "' is bound twice");
      }
    }

    for (ContextRelationValue relation : proto.getRelationsList()) {
      String name = require(relation.getName(), "a context relation");
      if (relation.getRowType().getFieldsCount() == 0) {
        throw new IllegalArgumentException(
            "context relation '" + name + "' declares no columns; a list or a relation has at "
                + "least one");
      }
      boolean shape = shapeOnly || relation.getShape();
      if (shape) {
        if (relation.getRowsCount() != 0) {
          throw new IllegalArgumentException(
              "context relation '" + name + "' carries rows and says it is a shape. Bind the rows "
                  + "at execution (docs/design/16-entitlements.md §2, §2.1, D209, D232).");
        }
        shapes.add(name);
      }
      checkRows(name, relation);
      Relation bound =
          new Relation(
              name,
              relation.getKind() == ContextRelationKind.UNRECOGNIZED
                  ? ContextRelationKind.CONTEXT_RELATION_KIND_LIST
                  : relation.getKind(),
              relation.getRowType(),
              relation.getRowsList());
      if (relations.put(name, bound) != null || scalars.containsKey(name)) {
        throw new IllegalArgumentException("context name '" + name + "' is bound twice");
      }
    }

    return new BoundContext(
        ImmutableMap.copyOf(scalars),
        ImmutableMap.copyOf(relations),
        ceiling(proto.getFoldMaxRows()),
        shapeOnly,
        ImmutableSet.copyOf(shapes));
  }

  /**
   * Every row has one value per declared column, every value is a literal, and — for a list — none
   * of them is NULL.
   *
   * <p>The NULL rule is not fussiness. A membership test against a set holding NULL is UNKNOWN for
   * every non-matching row and {@code NOT IN} is then never true, so a NULL in a tenancy list would
   * silently empty a result; and Calcite converts a literal {@code IN} list to an OR chain only when
   * the list holds no NULL, so one NULL member would also change the plan's shape. Refusing it at
   * bind is the only place a host can be told which binding was wrong.
   */
  private static void checkRows(String name, ContextRelationValue relation) {
    int columns = relation.getRowType().getFieldsCount();
    boolean isList = relation.getKind() != ContextRelationKind.CONTEXT_RELATION_KIND_RELATION;
    for (int r = 0; r < relation.getRowsCount(); r++) {
      VirtualRow row = relation.getRows(r);
      if (row.getValuesCount() != columns) {
        throw new IllegalArgumentException(
            "context relation '" + name + "' row " + r + " has " + row.getValuesCount()
                + " values and the row type declares " + columns);
      }
      for (int c = 0; c < row.getValuesCount(); c++) {
        Expr value = row.getValues(c);
        if (value.getKindCase() != Expr.KindCase.LITERAL) {
          throw new IllegalArgumentException(
              "context relation '" + name + "' row " + r + " column " + c + " is not a literal");
        }
        if (isList && isNull(value.getLiteral())) {
          throw new IllegalArgumentException(
              "context list '" + name + "' has a NULL in row " + r + " column " + c + ". A list is "
                  + "a membership set and is NOT NULL in every column: a NULL member makes every "
                  + "non-matching row UNKNOWN and NOT IN never true, which would silently empty a "
                  + "result rather than widen one. Bind a relation if the NULL is meant.");
        }
      }
    }
  }

  private static boolean isNull(Literal literal) {
    return literal.getValueCase() == Literal.ValueCase.IS_NULL && literal.getIsNull();
  }

  private static String require(String name, String what) {
    if (name == null || name.isBlank()) {
      throw new IllegalArgumentException(what + " has no name");
    }
    return name;
  }

  private static int ceiling(int declared) {
    if (declared < 0) {
      throw new IllegalArgumentException("fold_max_rows is negative");
    }
    return declared == 0 ? DEFAULT_FOLD_MAX_ROWS : declared;
  }

  /** Whether anything at all is bound. An empty context installs no schema and folds nothing. */
  public boolean isEmpty() {
    return scalars.isEmpty() && relations.isEmpty();
  }

  public int foldMaxRows() {
    return foldMaxRows;
  }

  /**
   * Whether this context is a <em>shape</em>: names, kinds and types with no values (D209).
   *
   * <p>Nothing folds under it. Every scalar is planned as a {@code DynamicParam} carrying its own
   * name, every list and relation as a {@code BoundTable}, and the sanitisers stay per-row — so two
   * principals whose bindings have the same shape share one plan and one digest.
   */
  public boolean shapeOnly() {
    return shapeOnly || (!isEmpty() && shapes.size() == scalars.size() + relations.size());
  }

  /**
   * Whether <em>any</em> name is bound as a shape — partial binding (D232), of which D209's
   * shape-only context is the case where every one of them is.
   *
   * <p>This is the question the pass asks, because one shape in a leaf's rules is enough to make
   * that leaf's memberships marker columns: what folds is literal beside them, in the same leaf.
   */
  public boolean anyShape() {
    return !shapes.isEmpty();
  }

  /** Whether this one name was bound as a shape rather than as a value (D232). */
  public boolean isShape(String name) {
    return shapes.contains(name);
  }

  /** The names bound as shapes, in the order the host sent them. */
  public ImmutableSet<String> shapes() {
    return shapes;
  }

  /**
   * The position of a bound scalar among them, in the order the host sent them — the slot a plan's
   * {@code bound_key} parameter takes, and the same for every table of one request.
   */
  public int scalarSlot(String name) {
    int slot = 0;
    for (String bound : scalars.keySet()) {
      if (bound.equals(name)) {
        return slot;
      }
      slot++;
    }
    return -1;
  }

  public ImmutableMap<String, Expr> scalars() {
    return scalars;
  }

  public ImmutableMap<String, Relation> relations() {
    return relations;
  }

  public Expr scalar(String name) {
    return scalars.get(name);
  }

  public Relation relation(String name) {
    return relations.get(name);
  }

  /** Whether this name is bound at all, as either kind. */
  public boolean isBound(String name) {
    return scalars.containsKey(name) || relations.containsKey(name);
  }

  /**
   * The relations that stay relations: the declared ones and every list too large to fold. These are
   * the tables of the per-request {@code ctx} schema, and the names the report lists as required at
   * execution.
   */
  public java.util.List<Relation> unfolded() {
    java.util.List<Relation> unfolded = new java.util.ArrayList<>();
    for (Relation relation : relations.values()) {
      if (isShape(relation.name()) || !relation.isList() || relation.rows().size() > foldMaxRows) {
        unfolded.add(relation);
      }
    }
    return unfolded;
  }
}
