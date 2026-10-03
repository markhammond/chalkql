package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.catchThrowableOfType;

import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.entitlement.PolicyException;
import chalk.planner.rpc.PlanErrorKind;
import chalk.planner.rpc.PlanErrors;
import chalk.planner.rpc.v1.PlanError;
import chalk.planner.rpc.v1.Violation;
import io.grpc.StatusRuntimeException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.List;
import java.util.regex.Pattern;
import org.apache.calcite.runtime.CalciteContextException;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;
import org.junit.jupiter.api.Test;

/**
 * Every error the planner sends names the rule it breaks (design 61): a violation carrying the code
 * its exception was raised with, or, for Calcite's own errors, SqlSyntax and SqlValidation, and the
 * kind of problem it is. Codes and kinds travel as names, which the glossary lists.
 */
class PlanErrorCodesTest {

  private static final Path REPO_ROOT =
      Path.of(System.getProperty("chalk.projectDir", ".")).toAbsolutePath().normalize().getParent();

  @Test
  void a_statement_that_does_not_parse_is_sql_syntax() throws Exception {
    SqlParseException parse =
        catchThrowableOfType(
            SqlParseException.class, () -> SqlParser.create("SELEC 1").parseQuery());

    Violation violation = only(sent(parse));

    assertThat(violation.getKind()).isEqualTo("Parse");
    assertThat(violation.getCode()).isEqualTo("SqlSyntax");
  }

  @Test
  void a_statement_calcite_refuses_to_validate_is_sql_validation() throws Exception {
    Violation violation =
        only(sent(new CalciteContextException("Column 'X' not found", new RuntimeException("X"))));

    assertThat(violation.getKind()).isEqualTo("Validation");
    assertThat(violation.getCode()).isEqualTo("SqlValidation");
  }

  @Test
  void the_planners_own_errors_carry_the_code_they_were_raised_with() throws Exception {
    assertThat(only(sent(new InvalidCatalogException(ErrorCode.DUPLICATE_NAME, "schemas", "x"))))
        .extracting(Violation::getCode, Violation::getKind)
        .containsExactly("DuplicateName", "InvalidCatalog");
    assertThat(
            only(
                sent(
                    new UnsupportedFeatureException(
                        ErrorCode.UNSUPPORTED_FUNCTION, "function F", "Not implemented."))))
        .extracting(Violation::getCode, Violation::getKind)
        .containsExactly("UnsupportedFunction", "Unsupported");
    assertThat(only(sent(new InvalidArgumentException(ErrorCode.INVALID_CONTEXT, "unbound"))))
        .extracting(Violation::getCode, Violation::getKind)
        .containsExactly("InvalidContext", "Validation");
  }

  @Test
  void a_name_the_planner_keeps_for_itself_is_reserved_name_whatever_its_kind_says()
      throws Exception {
    ReservedNames.ReservedNameException reserved =
        catchThrowableOfType(
            ReservedNames.ReservedNameException.class,
            () -> ReservedNames.check("SELECT 1 AS \"$chalk$x\"", "the statement"));

    Violation violation = only(sent(reserved));

    assertThat(violation.getKind()).isEqualTo("InvalidRequest");
    assertThat(violation.getCode()).isEqualTo("ReservedName");
  }

  @Test
  void an_argument_refused_without_a_rule_is_sql_validation_and_anything_else_internal()
      throws Exception {
    assertThat(only(sent(new IllegalArgumentException("bad"))).getCode())
        .isEqualTo("SqlValidation");
    assertThat(only(sent(new IllegalStateException("unreachable"))))
        .extracting(Violation::getCode, Violation::getKind)
        .containsExactly("Internal", "Internal");
  }

  /** What was refused travels with the rule it breaks, and the error's message is the violation's. */
  @Test
  void a_refusal_carries_its_code_and_what_it_refused_in_its_violation() throws Exception {
    PlanError error =
        sent(
            new PolicyException(
                ErrorCode.POPULATION_ONLY,
                "main.members",
                "national_id",
                "a projection to the result",
                List.of("COUNT"),
                "refused"));

    Violation violation = only(error);
    assertThat(violation.getCode()).isEqualTo("PopulationOnly");
    assertThat(violation.getKind()).isEqualTo("Policy");
    assertThat(violation.getMessage()).isEqualTo("refused").isEqualTo(error.getMessage());
    assertThat(violation.getTable()).isEqualTo("main.members");
    assertThat(violation.getColumn()).isEqualTo("national_id");
    assertThat(violation.getUse()).isEqualTo("a projection to the result");
    assertThat(violation.getPermittedList()).containsExactly("COUNT");
  }

  /**
   * A code is a name the glossary explains, so the planner names no code the glossary does not,
   * and leaves out none it does. A kind is named the way its constant is spelt.
   */
  @Test
  void every_code_is_one_the_glossary_explains() throws Exception {
    String glossary = Files.readString(REPO_ROOT.resolve("docs/errors.md"));
    List<String> entries =
        Pattern.compile("^### (\\w+)\\s*$", Pattern.MULTILINE)
            .matcher(glossary)
            .results()
            .map(match -> match.group(1))
            .toList();

    assertThat(Arrays.stream(ErrorCode.values()).map(ErrorCode::wireName))
        .containsExactlyInAnyOrderElementsOf(entries);
    assertThat(PlanErrorKind.values())
        .allSatisfy(
            kind ->
                assertThat(kind.wireName().replaceAll("([a-z])([A-Z])", "$1_$2").toUpperCase())
                    .isEqualTo(kind.name()));
  }

  private static PlanError sent(Throwable error) throws Exception {
    StatusRuntimeException status = PlanErrors.toStatus(error);
    return PlanError.parseFrom(status.getTrailers().get(PlanErrors.TRAILER));
  }

  /** The one rule an error breaks: the planner stops at the first. */
  private static Violation only(PlanError error) {
    assertThat(error.getViolationsList()).hasSize(1);
    return error.getViolations(0);
  }
}
