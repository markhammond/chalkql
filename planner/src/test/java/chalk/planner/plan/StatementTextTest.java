package chalk.planner.plan;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.FunctionDescriptor;
import chalk.ir.v1.FunctionKind;
import chalk.ir.v1.Parameter;
import chalk.ir.v1.SqlBody;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.Volatility;
import chalk.planner.TestCatalogs;
import chalk.planner.UnsupportedFeatureException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.catalog.UserFunctions;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.junit.jupiter.api.Test;

/**
 * A refusal in the statement's own words (D296): the span between a node's parser positions,
 * trimmed and bounded, and the validated form wherever no span of the statement can be recovered.
 */
final class StatementTextTest {

  private static void plan(CatalogContext catalog, String sql) {
    RegisteredCatalog registered = RegisteredCatalog.of(catalog);
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      pipeline.plan(sql, false);
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  private static void plan(String sql) {
    plan(TestCatalogs.corpus(), sql);
  }

  @Test
  void a_line_and_column_index_the_text_as_the_parser_counts_them() {
    String text = "ab\ncd\r\nef\rgh\tij";
    assertThat(StatementText.index(text, 1, 1)).isZero();
    assertThat(text.charAt(StatementText.index(text, 2, 2))).isEqualTo('d');
    // \r\n is one line end, and so is a lone \r.
    assertThat(text.charAt(StatementText.index(text, 3, 1))).isEqualTo('e');
    assertThat(text.charAt(StatementText.index(text, 4, 1))).isEqualTo('g');
    // A tab is one column.
    assertThat(text.charAt(StatementText.index(text, 4, 4))).isEqualTo('i');
    // Past the end, or a line that is not there, is no index at all.
    assertThat(StatementText.index(text, 4, 20)).isEqualTo(-1);
    assertThat(StatementText.index(text, 9, 1)).isEqualTo(-1);
  }

  @Test
  void a_span_is_trimmed_made_one_line_and_bounded() {
    assertThat(StatementText.bounded("  price_move(\n    \"open\",\r\n    \"close\")  "))
        .isEqualTo("price_move( \"open\", \"close\")");
    String longest = "x".repeat(StatementText.LONGEST);
    assertThat(StatementText.bounded(longest)).isEqualTo(longest);
    String cut = StatementText.bounded(longest + "y");
    assertThat(cut).hasSize(StatementText.LONGEST).endsWith("…");
  }

  @Test
  void a_node_with_no_position_or_no_text_prints_as_calcite_prints_it() {
    SqlIdentifier synthesised = new SqlIdentifier("MOVE", SqlParserPos.ZERO);
    StatementText text =
        StatementText.of(
            "SELECT move FROM bars",
            SqlConfigs.parser(SqlConfigs.DEFAULT_CONFORMANCE),
            UserFunctions.of(TestCatalogs.corpus()));
    assertThat(text.quote(synthesised)).isEqualTo("MOVE");
    assertThat(StatementText.NONE.quote(new SqlIdentifier("m", new SqlParserPos(1, 8, 1, 11))))
        .isEqualTo("m");
  }

  @Test
  void a_refusal_over_several_lines_quotes_them_as_one() {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol\n"
                        + "FROM bars\n"
                        + "ORDER BY price_move(\n"
                        + "    \"open\",\t\"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a composite value (price_move( \"open\",\t\"close\"))");
  }

  @Test
  void a_refusal_quotes_what_the_statement_wrote_and_not_the_validated_form() {
    // The validated call is qualified and its arguments cast to the declared parameter types; the
    // statement wrote neither.
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT symbol FROM bars GROUP BY symbol, PRICE_MOVE(\"open\" , \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("GROUP BY a composite value (PRICE_MOVE(\"open\" , \"close\"))")
        .hasMessageNotContaining("CAST");
  }

  @Test
  void a_node_a_sql_body_brought_in_is_quoted_in_its_validated_form() {
    // same_move's body compares two composites. The comparison refused is the body's, whose
    // positions index the body's text: quoting the statement at them would name something the
    // statement never wrote, so the refusal prints the node as Calcite does instead.
    Type bool = TestCatalogs.nullable(TypeKind.TYPE_KIND_BOOL);
    Type fp64 = TestCatalogs.nullable(TypeKind.TYPE_KIND_FP64);
    FunctionDescriptor sameMove =
        FunctionDescriptor.newBuilder()
            .setName("same_move")
            .setKind(FunctionKind.FUNCTION_KIND_SCALAR)
            .setVolatility(Volatility.VOLATILITY_IMMUTABLE)
            .setReturnType(bool)
            .addParameters(Parameter.newBuilder().setName("a").setType(fp64))
            .addParameters(Parameter.newBuilder().setName("b").setType(fp64))
            .setSql(SqlBody.newBuilder().setText("price_move(a, b) = price_move(b, a)"))
            .build();
    CatalogContext.Builder catalog = TestCatalogs.corpus().toBuilder();
    catalog.getSchemasBuilder(0).addFunctions(sameMove);

    assertThatThrownBy(
            () -> plan(catalog.build(), "SELECT symbol FROM bars WHERE same_move(\"open\", \"close\")"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a comparison of a composite value (")
        .hasMessageNotContaining("same_move(\"open\", \"close\")")
        .hasMessageContaining("`main`.`price_move`(");
  }
}
