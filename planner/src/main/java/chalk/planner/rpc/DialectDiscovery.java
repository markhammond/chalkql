package chalk.planner.rpc;

import chalk.planner.plan.SourceDialects;
import chalk.planner.rpc.v1.DialectInfo;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.validate.SqlConformanceEnum;

/**
 * The three {@code GetInfo} lists D248 added — every dialect preset, conformance level and library
 * this sidecar accepts (design 31, ADR 0032). None of it is per-request, so {@link
 * PlannerServiceImpl#getInfo} asks for the same three lists on every call.
 *
 * <p>Split from {@link chalk.planner.plan.SourceDialects}, which knows Calcite dialects but not this
 * service's wire types: {@link SourceDialects#presets()} is the resolution, this is that resolution
 * turned into {@code DialectInfo} messages, plus the two conformance/library enumerations that are
 * not about dialects at all.
 */
final class DialectDiscovery {
  private DialectDiscovery() {}

  static List<DialectInfo> dialects() {
    List<DialectInfo> infos = new ArrayList<>();
    for (SourceDialects.DialectPreset preset : SourceDialects.presets()) {
      infos.add(
          DialectInfo.newBuilder()
              .setName(preset.name())
              .setDatabaseProduct(preset.databaseProduct())
              .setTuned(preset.tuned())
              .addAllAliases(preset.aliases())
              .build());
    }
    return infos;
  }

  /**
   * Every level this build of Calcite has, by name (D248, D318) — the planner's parser accepts
   * every one, via {@code SqlConfigs.conformance}, so the honest list is the whole enum.
   */
  static List<String> conformances() {
    return Arrays.stream(SqlConformanceEnum.values()).map(Enum::name).toList();
  }

  /**
   * Every library this build of Calcite has, by name (D248, D318), {@code STANDARD} included: it is
   * a real library, always available, per {@code SqlConfigs.libraries}.
   */
  static List<String> libraries() {
    return Arrays.stream(SqlLibrary.values()).map(Enum::name).toList();
  }
}
