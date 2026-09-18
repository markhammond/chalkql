package chalk.planner.rpc;

import chalk.ir.v1.SqlConformance;
import chalk.ir.v1.SqlLibrary;
import chalk.planner.plan.SourceDialects;
import chalk.planner.rpc.v1.DialectInfo;
import java.util.ArrayList;
import java.util.List;

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
   * Every level {@code dialect.proto}'s {@code SqlConformance} names, mirroring Calcite's one for
   * one (D248) — the planner's parser accepts every one, via {@code SqlConfigs.conformance}, so the
   * honest list is the whole enum. {@code UNSPECIFIED} is the wire's "nothing said" marker, not a
   * level, and {@code UNRECOGNIZED} is protobuf's own placeholder for a number this build has never
   * heard of; neither is something the sidecar "accepts".
   */
  static List<SqlConformance> conformances() {
    List<SqlConformance> levels = new ArrayList<>();
    for (SqlConformance level : SqlConformance.values()) {
      if (level != SqlConformance.SQL_CONFORMANCE_UNSPECIFIED && level != SqlConformance.UNRECOGNIZED) {
        levels.add(level);
      }
    }
    return levels;
  }

  /**
   * Every library {@code dialect.proto}'s {@code SqlLibrary} names (D248), {@code STANDARD}
   * included: it is a real, distinct enum value (always available, per {@code SqlConfigs.libraries}),
   * not the wire's "nothing said" marker — that is {@code UNSPECIFIED}, excluded for the reason
   * {@link #conformances} excludes it, along with {@code UNRECOGNIZED}.
   */
  static List<SqlLibrary> libraries() {
    List<SqlLibrary> libraries = new ArrayList<>();
    for (SqlLibrary library : SqlLibrary.values()) {
      if (library != SqlLibrary.SQL_LIBRARY_UNSPECIFIED && library != SqlLibrary.UNRECOGNIZED) {
        libraries.add(library);
      }
    }
    return libraries;
  }
}
