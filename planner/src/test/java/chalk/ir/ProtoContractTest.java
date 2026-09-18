package chalk.ir;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Field;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Read;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RowType;
import chalk.ir.v1.TableRef;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import com.google.protobuf.InvalidProtocolBufferException;
import org.junit.jupiter.api.Test;

/**
 * The Java half of "both builds compile proto/ and agree on it" (work plan step 1). It only has to
 * prove the generated code exists and round-trips; semantics are RelToIrTest's job.
 */
class ProtoContractTest {

  @Test
  void plan_round_trips_through_the_wire_format() throws InvalidProtocolBufferException {
    Plan plan = samplePlan();

    byte[] bytes = plan.toByteArray();
    Plan parsed = Plan.parseFrom(bytes);

    assertThat(parsed).isEqualTo(plan);
    assertThat(parsed.getIrVersion()).isEqualTo(IrVersion.CURRENT);
    assertThat(parsed.getRoot().getKindCase()).isEqualTo(Rel.KindCase.READ);
    assertThat(parsed.getRoot().getRead().getTable().getTable()).isEqualTo("bars");
  }

  @Test
  void unset_oneof_is_distinguishable_from_a_set_one() {
    assertThat(Rel.getDefaultInstance().getKindCase()).isEqualTo(Rel.KindCase.KIND_NOT_SET);
    assertThat(samplePlan().getRoot().getKindCase()).isNotEqualTo(Rel.KindCase.KIND_NOT_SET);
  }

  private static Plan samplePlan() {
    RowType rowType =
        RowType.newBuilder()
            .addFields(
                Field.newBuilder()
                    .setName("symbol")
                    .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_STRING)))
            .build();
    Rel read =
        Rel.newBuilder()
            .setRowType(rowType)
            .setEstRowCount(100_800)
            .setRead(
                Read.newBuilder()
                    .setTable(
                        TableRef.newBuilder().setSourceId("mem").setSchema("main").setTable("bars"))
                    .addProjection(0))
            .build();
    return Plan.newBuilder()
        .setIrVersion(IrVersion.CURRENT)
        .setContextId("demo")
        .setCatalogEpoch(1)
        .setOutputType(rowType)
        .setRoot(read)
        .build();
  }
}
