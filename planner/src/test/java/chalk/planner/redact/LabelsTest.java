package chalk.planner.redact;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.Literal;
import chalk.ir.v1.RowType;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.VirtualRow;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.rpc.v1.ContextRelationKind;
import chalk.planner.rpc.v1.ContextRelationValue;
import chalk.planner.rpc.v1.ContextScalar;
import chalk.planner.rpc.v1.RequestContext;
import org.junit.jupiter.api.Test;

/**
 * What a request's bound values become as labels (D286): a scalar under its name, a list's elements
 * under the list's, and the things that are never labelled.
 */
class LabelsTest {
  static Type i32() {
    return Type.newBuilder().setKind(TypeKind.TYPE_KIND_I32).build();
  }

  static Type string() {
    return Type.newBuilder().setKind(TypeKind.TYPE_KIND_STRING).build();
  }

  static Expr literal(int value) {
    return Expr.newBuilder()
        .setType(i32())
        .setLiteral(Literal.newBuilder().setI32Value(value))
        .build();
  }

  static Expr literal(String value) {
    return Expr.newBuilder()
        .setType(string())
        .setLiteral(Literal.newBuilder().setStringValue(value))
        .build();
  }

  static ContextScalar scalar(String name, Expr value) {
    return ContextScalar.newBuilder().setName(name).setValue(value).build();
  }

  static ContextRelationValue list(String name, int... values) {
    ContextRelationValue.Builder builder =
        ContextRelationValue.newBuilder()
            .setName(name)
            .setKind(ContextRelationKind.CONTEXT_RELATION_KIND_LIST)
            .setRowType(
                RowType.newBuilder().addFields(Field.newBuilder().setName("id").setType(i32())));
    for (int value : values) {
      builder.addRows(VirtualRow.newBuilder().addValues(literal(value)));
    }
    return builder.build();
  }

  static Labels labels(RequestContext.Builder context) {
    return Labels.of(BoundContext.of(context.build()));
  }

  @Test
  void an_empty_context_has_no_labels() {
    assertThat(Labels.of(BoundContext.EMPTY)).isSameAs(Labels.NONE);
    assertThat(labels(RequestContext.newBuilder()).isEmpty()).isTrue();
  }

  @Test
  void a_scalar_is_labelled_under_its_name_and_a_list_element_under_the_lists() {
    Labels labels =
        labels(
            RequestContext.newBuilder()
                .addScalars(scalar("org", literal(7)))
                .addScalars(scalar("sym", literal("BTCUSDT")))
                .addRelations(list("orgs", 1, 2)));

    assertThat(labels.of("DECIMAL", "7")).isEqualTo("@ctx.org");
    assertThat(labels.of("CHAR", "'BTCUSDT'")).isEqualTo("@ctx.sym");
    assertThat(labels.of("DECIMAL", "1")).isEqualTo("@ctx.orgs");
    assertThat(labels.of("DECIMAL", "2")).isEqualTo("@ctx.orgs");
    assertThat(labels.of("DECIMAL", "9")).isNull();
    // The type is part of the key: a string '7' is not the integer 7.
    assertThat(labels.of("CHAR", "'7'")).isNull();
  }

  /** A value bound under two names carries both — each is a true thing to say of it. */
  @Test
  void a_value_bound_under_two_names_carries_both() {
    Labels labels =
        labels(
            RequestContext.newBuilder()
                .addScalars(scalar("user", literal(7)))
                .addRelations(list("orgs", 7)));

    assertThat(labels.of("DECIMAL", "7")).isEqualTo("@ctx.user,@ctx.orgs");
  }

  /**
   * A NULL is never labelled, a shape-only name has no value to label, and a list above the fold
   * ceiling stays a relation whose rows are never in a plan.
   */
  @Test
  void a_null_a_shape_and_an_unfolded_list_are_not_labelled() {
    Expr nullValue =
        Expr.newBuilder()
            .setType(i32().toBuilder().setNullable(true))
            .setLiteral(Literal.newBuilder().setIsNull(true))
            .build();
    int[] tooMany = new int[BoundContext.DEFAULT_FOLD_MAX_ROWS + 1];
    for (int i = 0; i < tooMany.length; i++) {
      tooMany[i] = 100 + i;
    }
    Labels labels =
        labels(
            RequestContext.newBuilder()
                .addScalars(scalar("missing", nullValue))
                .addScalars(
                    ContextScalar.newBuilder()
                        .setName("later")
                        .setValue(Expr.newBuilder().setType(i32()))
                        .setShape(true))
                .addRelations(list("everyone", tooMany)));

    assertThat(labels.isEmpty()).isTrue();
    assertThat(labels.of("NULL", "NULL")).isNull();
    assertThat(labels.of("DECIMAL", "5")).isNull();
    assertThat(labels.of("DECIMAL", "100")).isNull();
  }
}
