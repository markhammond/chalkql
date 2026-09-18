package chalk.ir;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Aggregate;
import chalk.ir.v1.AggregateFunctionId;
import chalk.ir.v1.Collation;
import chalk.ir.v1.DecimalValue;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.FieldRef;
import chalk.ir.v1.Filter;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.Grouping;
import chalk.ir.v1.HashAggregate;
import chalk.ir.v1.IfClause;
import chalk.ir.v1.IfThen;
import chalk.ir.v1.Literal;
import chalk.ir.v1.Measure;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Project;
import chalk.ir.v1.Read;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RowType;
import chalk.ir.v1.ScalarCall;
import chalk.ir.v1.SortDirection;
import chalk.ir.v1.SortField;
import chalk.ir.v1.TableRef;
import chalk.ir.v1.TopN;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.VirtualRow;
import chalk.ir.v1.VirtualTable;
import com.google.protobuf.ByteString;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;
import org.junit.jupiter.api.Test;

/**
 * Writes (and, normally, verifies) the cross-language digest fixtures. {@code
 * Chalk.Ir.Tests.PlanDigestFixtureTests} recomputes every one of these with the C# implementation;
 * if the two ever disagree the build fails, which is the point (docs/design/05-testing.md §4).
 *
 * <p>Run with {@code CHALK_WRITE_FIXTURES=1} to regenerate after a deliberate IR change, then
 * review the diff.
 */
class DigestFixturesTest {

  private static final Path FIXTURES =
      Path.of(System.getProperty("chalk.projectDir", "."))
          .resolve("src/test/resources/digest-fixtures");

  @Test
  void fixtures_match_the_java_digest() throws IOException {
    boolean write = "1".equals(System.getenv("CHALK_WRITE_FIXTURES"));
    if (write) {
      Files.createDirectories(FIXTURES);
    }

    assertThat(fixtures()).hasSizeGreaterThanOrEqualTo(5);

    for (Map.Entry<String, Plan> entry : fixtures().entrySet()) {
      String name = entry.getKey();
      Plan plan = entry.getValue().toBuilder().setPlanDigest(0L).build();
      long digest = PlanDigest.compute(plan);
      Plan stamped = plan.toBuilder().setPlanDigest(digest).build();

      Path binpb = FIXTURES.resolve(name + ".binpb");
      Path digestFile = FIXTURES.resolve(name + ".digest");

      if (write) {
        Files.write(binpb, stamped.toByteArray());
        Files.writeString(digestFile, PlanDigest.format(digest) + "\n", StandardCharsets.UTF_8);
        continue;
      }

      assertThat(binpb).as("fixture %s; regenerate with CHALK_WRITE_FIXTURES=1", name).exists();
      Plan recorded = Plan.parseFrom(Files.readAllBytes(binpb));
      assertThat(recorded).as("fixture %s is still the plan this test builds", name).isEqualTo(stamped);
      assertThat(PlanDigest.compute(recorded))
          .as("fixture %s digest is stable", name)
          .isEqualTo(digest);
      assertThat(Files.readString(digestFile, StandardCharsets.UTF_8).trim())
          .as("fixture %s .digest file", name)
          .isEqualTo(PlanDigest.format(digest));
    }
  }

  @Test
  void the_digest_ignores_estimates_context_and_epoch_but_not_shape() {
    Plan plan = scanAll();
    long baseline = PlanDigest.compute(plan);

    assertThat(
            PlanDigest.compute(
                plan.toBuilder()
                    .setContextId("something-else")
                    .setCatalogEpoch(99)
                    .setRoot(plan.getRoot().toBuilder().setEstRowCount(17.0))
                    .build()))
        .isEqualTo(baseline);

    assertThat(
            PlanDigest.compute(
                plan.toBuilder()
                    .setRoot(
                        plan.getRoot().toBuilder()
                            .setRead(plan.getRoot().getRead().toBuilder().clearProjection().addProjection(0)))
                    .build()))
        .isNotEqualTo(baseline);
  }

  @Test
  void estimates_are_stripped_at_every_depth() {
    Plan plan = filterProject();
    Rel project = plan.getRoot();
    Rel filter = project.getProject().getInput();
    Rel read = filter.getFilter().getInput();

    Plan perturbed =
        plan.toBuilder()
            .setRoot(
                project.toBuilder()
                    .setEstRowCount(1)
                    .setProject(
                        project.getProject().toBuilder()
                            .setInput(
                                filter.toBuilder()
                                    .setEstRowCount(2)
                                    .setFilter(
                                        filter.getFilter().toBuilder()
                                            .setInput(read.toBuilder().setEstRowCount(3))))))
            .build();

    assertThat(PlanDigest.compute(perturbed)).isEqualTo(PlanDigest.compute(plan));
  }

  /** The fixture set. Names are stable; adding one is fine, renaming one churns the C# test data. */
  static Map<String, Plan> fixtures() {
    Map<String, Plan> map = new LinkedHashMap<>();
    map.put("01_scan_all", scanAll());
    map.put("02_filter_project", filterProject());
    map.put("03_hash_aggregate", hashAggregate());
    map.put("04_topn_case_decimal", topNCaseDecimal());
    map.put("05_virtual_table_params", virtualTableParams());
    return map;
  }

  // ---- fixture plans ----

  private static Plan scanAll() {
    RowType row = barsRow();
    Rel read =
        rel(row, 100_800)
            .setRead(
                Read.newBuilder()
                    .setTable(table("bars"))
                    .addAllProjection(indexes(row.getFieldsCount())))
            .addCollations(
                Collation.newBuilder()
                    .addFields(
                        sortField(
                            1,
                            Type.newBuilder()
                                .setKind(TypeKind.TYPE_KIND_TIMESTAMP)
                                .setPrecision(9)
                                .build(),
                            SortDirection.SORT_DIRECTION_ASC_NULLS_LAST))
                    .addFields(
                        sortField(
                            0,
                            type(TypeKind.TYPE_KIND_STRING, false),
                            SortDirection.SORT_DIRECTION_ASC_NULLS_LAST)))
            .build();
    return plan(read);
  }

  private static Plan filterProject() {
    RowType row = barsRow();
    Rel read =
        rel(row, 100_800)
            .setRead(
                Read.newBuilder()
                    .setTable(table("bars"))
                    .addAllProjection(indexes(row.getFieldsCount())))
            .build();
    Expr condition =
        call(
            FunctionId.FUNCTION_ID_EQ,
            type(TypeKind.TYPE_KIND_BOOL, false),
            fieldRef(0, type(TypeKind.TYPE_KIND_STRING, false)),
            stringLiteral("BTCUSDT"));
    Rel filter =
        rel(row, 20_160).setFilter(Filter.newBuilder().setInput(read).setCondition(condition)).build();

    RowType projected =
        RowType.newBuilder()
            .addFields(field("symbol", type(TypeKind.TYPE_KIND_STRING, false)))
            .addFields(field("close", type(TypeKind.TYPE_KIND_FP64, false)))
            .build();
    Rel project =
        rel(projected, 20_160)
            .setProject(
                Project.newBuilder()
                    .setInput(filter)
                    .addExprs(fieldRef(0, type(TypeKind.TYPE_KIND_STRING, false)))
                    .addExprs(fieldRef(5, type(TypeKind.TYPE_KIND_FP64, false))))
            .build();
    return plan(project);
  }

  private static Plan hashAggregate() {
    RowType row = barsRow();
    Rel read =
        rel(row, 100_800)
            .setRead(
                Read.newBuilder()
                    .setTable(table("bars"))
                    .addAllProjection(indexes(row.getFieldsCount())))
            .build();
    Aggregate aggregate =
        Aggregate.newBuilder()
            .setInput(read)
            .addGroupings(Grouping.newBuilder().addKeys(0))
            .addMeasures(
                Measure.newBuilder()
                    .setFunction(AggregateFunctionId.AGGREGATE_FUNCTION_ID_COUNT)
                    .setType(type(TypeKind.TYPE_KIND_I64, false)))
            .addMeasures(
                Measure.newBuilder()
                    .setFunction(AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM)
                    .addArgs(fieldRef(6, type(TypeKind.TYPE_KIND_I64, false)))
                    .setType(type(TypeKind.TYPE_KIND_I64, true)))
            .build();
    RowType out =
        RowType.newBuilder()
            .addFields(field("symbol", type(TypeKind.TYPE_KIND_STRING, false)))
            .addFields(field("n", type(TypeKind.TYPE_KIND_I64, false)))
            .addFields(field("vol", type(TypeKind.TYPE_KIND_I64, true)))
            .build();
    Rel agg =
        rel(out, 5).setHashAggregate(HashAggregate.newBuilder().setAggregate(aggregate)).build();
    return plan(agg);
  }

  private static Plan topNCaseDecimal() {
    RowType row = barsRow();
    Rel read =
        rel(row, 100_800)
            .setRead(
                Read.newBuilder()
                    .setTable(table("bars"))
                    .addAllProjection(indexes(row.getFieldsCount())))
            .build();

    Type str = type(TypeKind.TYPE_KIND_STRING, false);
    Type fp64 = type(TypeKind.TYPE_KIND_FP64, false);
    Expr direction =
        Expr.newBuilder()
            .setType(str)
            .setIfThen(
                IfThen.newBuilder()
                    .addClauses(
                        IfClause.newBuilder()
                            .setCondition(
                                call(
                                    FunctionId.FUNCTION_ID_GT,
                                    type(TypeKind.TYPE_KIND_BOOL, false),
                                    fieldRef(5, fp64),
                                    fieldRef(2, fp64)))
                            .setResult(stringLiteral("up")))
                    .setElseBranch(stringLiteral("down")))
            .build();

    Type decimal =
        Type.newBuilder()
            .setKind(TypeKind.TYPE_KIND_DECIMAL)
            .setPrecision(28)
            .setScale(10)
            .setNullable(true)
            .build();
    Expr vwapOrZero =
        call(
            FunctionId.FUNCTION_ID_COALESCE,
            decimal,
            fieldRef(7, decimal),
            Expr.newBuilder()
                .setType(decimal)
                .setLiteral(
                    Literal.newBuilder()
                        .setDecimalValue(
                            DecimalValue.newBuilder()
                                .setUnscaled(ByteString.copyFrom(new byte[16]))))
                .build());

    RowType out =
        RowType.newBuilder()
            .addFields(field("dir", str))
            .addFields(field("vwap", decimal))
            .build();
    Rel project =
        rel(out, 100_800)
            .setProject(Project.newBuilder().setInput(read).addExprs(direction).addExprs(vwapOrZero))
            .build();
    Rel topN =
        rel(out, 10)
            .setTopN(
                TopN.newBuilder()
                    .setInput(project)
                    .addFields(sortField(1, decimal, SortDirection.SORT_DIRECTION_DESC_NULLS_FIRST))
                    .setOffset(0)
                    .setCount(10))
            .addCollations(
                Collation.newBuilder()
                    .addFields(sortField(1, decimal, SortDirection.SORT_DIRECTION_DESC_NULLS_FIRST)))
            .build();
    return plan(topN);
  }

  private static Plan virtualTableParams() {
    Type i32 = type(TypeKind.TYPE_KIND_I32, false);
    Type str = type(TypeKind.TYPE_KIND_STRING, true);
    RowType out =
        RowType.newBuilder().addFields(field("one", i32)).addFields(field("s", str)).build();
    Rel values =
        rel(out, 1)
            .setVirtualTable(
                VirtualTable.newBuilder()
                    .addRows(
                        VirtualRow.newBuilder()
                            .addValues(
                                Expr.newBuilder()
                                    .setType(i32)
                                    .setLiteral(Literal.newBuilder().setI32Value(1)))
                            .addValues(
                                Expr.newBuilder()
                                    .setType(str)
                                    .setLiteral(Literal.newBuilder().setIsNull(true)))))
            .build();
    return Plan.newBuilder()
        .setIrVersion(IrVersion.CURRENT)
        .setContextId("fixtures")
        .setCatalogEpoch(1)
        .setOutputType(out)
        .setRoot(values)
        .addParameterTypes(type(TypeKind.TYPE_KIND_STRING, true))
        .addParameterTypes(
            Type.newBuilder()
                .setKind(TypeKind.TYPE_KIND_TIMESTAMP)
                .setPrecision(9)
                .setNullable(true)
                .build())
        .build();
  }

  // ---- builders ----

  private static RowType barsRow() {
    return RowType.newBuilder()
        .addFields(field("symbol", type(TypeKind.TYPE_KIND_STRING, false)))
        .addFields(
            field(
                "ts",
                Type.newBuilder()
                    .setKind(TypeKind.TYPE_KIND_TIMESTAMP)
                    .setPrecision(9)
                    .build()))
        .addFields(field("open", type(TypeKind.TYPE_KIND_FP64, false)))
        .addFields(field("high", type(TypeKind.TYPE_KIND_FP64, false)))
        .addFields(field("low", type(TypeKind.TYPE_KIND_FP64, false)))
        .addFields(field("close", type(TypeKind.TYPE_KIND_FP64, false)))
        .addFields(field("volume", type(TypeKind.TYPE_KIND_I64, false)))
        .addFields(
            field(
                "vwap",
                Type.newBuilder()
                    .setKind(TypeKind.TYPE_KIND_DECIMAL)
                    .setPrecision(28)
                    .setScale(10)
                    .setNullable(true)
                    .build()))
        .addFields(field("trade_count", type(TypeKind.TYPE_KIND_I32, true)))
        .build();
  }

  private static Plan plan(Rel root) {
    return Plan.newBuilder()
        .setIrVersion(IrVersion.CURRENT)
        .setContextId("fixtures")
        .setCatalogEpoch(1)
        .setOutputType(root.getRowType())
        .setRoot(root)
        .build();
  }

  private static Rel.Builder rel(RowType rowType, double rows) {
    return Rel.newBuilder().setRowType(rowType).setEstRowCount(rows);
  }

  private static TableRef table(String name) {
    return TableRef.newBuilder().setSourceId("mem").setSchema("main").setTable(name).build();
  }

  private static Iterable<Integer> indexes(int count) {
    return java.util.stream.IntStream.range(0, count).boxed().toList();
  }

  private static Field field(String name, Type type) {
    return Field.newBuilder().setName(name).setType(type).build();
  }

  private static Type type(TypeKind kind, boolean nullable) {
    return Type.newBuilder().setKind(kind).setNullable(nullable).build();
  }

  private static Expr fieldRef(int index, Type type) {
    return Expr.newBuilder()
        .setType(type)
        .setFieldRef(FieldRef.newBuilder().setIndex(index))
        .build();
  }

  private static Expr stringLiteral(String value) {
    return Expr.newBuilder()
        .setType(type(TypeKind.TYPE_KIND_STRING, false))
        .setLiteral(Literal.newBuilder().setStringValue(value))
        .build();
  }

  private static Expr call(FunctionId function, Type type, Expr... args) {
    ScalarCall.Builder call = ScalarCall.newBuilder().setFunction(function);
    for (Expr arg : args) {
      call.addArgs(arg);
    }
    return Expr.newBuilder().setType(type).setCall(call).build();
  }

  private static SortField sortField(int index, Type type, SortDirection direction) {
    return SortField.newBuilder()
        .setExpr(fieldRef(index, type))
        .setDirection(direction)
        .build();
  }
}
