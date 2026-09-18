package chalk.planner.entitlement;

import chalk.planner.ext.PlanExtensionContext;
import chalk.planner.ext.PlanExtensionHandler;
import chalk.planner.ext.PlanExtensionRegistry;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.rpc.v1.EntitledTable;
import chalk.planner.rpc.v1.EntitlementsExplain;
import chalk.planner.rpc.v1.EntitlementsOptions;
import chalk.planner.rpc.v1.EntitlementsReport;
import chalk.planner.rpc.v1.ExplainedColumn;
import chalk.planner.rpc.v1.ExplainedTable;
import chalk.planner.rpc.v1.Visibility;
import com.google.protobuf.Any;
import com.google.protobuf.InvalidProtocolBufferException;
import com.google.protobuf.Message;
import java.util.ArrayList;
import java.util.List;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The entitlement feature's handler (step 26c, docs/design/29-entitlements-as-a-wrapper.md §1,
 * D212): the one place on the sidecar that knows this request has a policy on it.
 *
 * <p>Hook 1 turns the request's {@code chalk.v1.EntitlementsOptions} — when it attached one — into
 * the {@link PolicyOptions} the pipeline's pass reads. Hook 3 writes the {@code
 * chalk.v1.EntitlementsReport}, and the {@code chalk.v1.EntitlementsExplain} a caller asked for, into
 * the response's extension slot, and only when the pass actually ran: the pass is installed by the
 * catalog rather than by the options, so a request that attached nothing can still have run it, and
 * a plan over a catalog with no entitlement writes nothing at all.
 */
public final class EntitlementExtension implements PlanExtensionHandler {

  /** The message this handler is registered for. */
  public static final String OPTIONS = "chalk.v1.EntitlementsOptions";

  @Override
  public String messageName() {
    return OPTIONS;
  }

  @Override
  public void beforeConversion(@Nullable Any message, PlanExtensionContext context) {
    // Absent means every default, which is what a statement gets when the client attaches nothing.
    // The pass itself is installed by the catalog: an entitled table is entitled whether or not the
    // caller had an opinion about stars and placeholders.
    if (message == null) {
      context.put(PolicyOptions.class, PolicyOptions.DEFAULTS);
      return;
    }
    EntitlementsOptions options;
    try {
      options = message.unpack(EntitlementsOptions.class);
    } catch (InvalidProtocolBufferException e) {
      throw PlanExtensionRegistry.UnknownExtensionException.malformed(message.getTypeUrl(), e);
    }
    context.put(PolicyOptions.class, PolicyOptions.of(options));
  }

  @Override
  public List<Message> onResponse(PlanExtensionContext context) {
    PlannerPipeline.Result result = context.get(PlannerPipeline.Result.class);
    if (result == null || result.entitledLeaves().isEmpty()) {
      return List.of();
    }
    PolicyOptions options = context.getOrDefault(PolicyOptions.class, PolicyOptions.DEFAULTS);
    List<Message> messages = new ArrayList<>(2);
    messages.add(report(result, options));
    if (options.explain()) {
      messages.add(explain(result));
    }
    return messages;
  }

  /**
   * What the caller is told about the rewrite (§3.12): what each output column discloses, what each
   * entitled table's descriptor was and how much of it this principal can see, and what execution
   * must still bind.
   */
  private static EntitlementsReport report(
      PlannerPipeline.Result result, PolicyOptions policyOptions) {
    EntitlementsReport.Builder report = EntitlementsReport.newBuilder();
    report.addAllColumns(result.columnDisclosures());
    report.addAllRequiredRelations(result.requiredRelations());
    // Under prepare-time binding every scalar was folded into a literal and there is nothing here;
    // under execute-time binding these are the names execution must bind (§2, D209).
    report.addAllRequiredScalars(result.requiredScalars());
    report.setOmitDegradedToPlaceholder(result.omitDegraded());

    for (DisclosureMap table : DisclosureReport.tables(result.entitledLeaves())) {
      report.addDescriptorHashes(table.descriptorHash());
      Visibility visibility =
          switch (table.visibility()) {
            case NONE -> Visibility.VISIBILITY_NONE;
            case ALL -> Visibility.VISIBILITY_ALL;
            default -> Visibility.VISIBILITY_SOME;
          };
      if (visibility == Visibility.VISIBILITY_NONE && policyOptions.refuseWhenNoVisibleRows()) {
        // D207: an acknowledgement may depend on the principal's own context and the statement,
        // never on hidden rows. That this principal holds no grant on the table at all is such an
        // acknowledgement, and a host may ask for an exception rather than an empty result.
        throw new PolicyException(
            "this principal holds no grant on "
                + table.qualifiedName()
                + ", so the statement can return no row, and "
                + "PrepareOptions.RefuseWhenNoVisibleRows asks for an error rather than an empty "
                + "result (docs/design/16-entitlements.md §3.12).");
      }
      if (!table.contradiction().isEmpty() && policyOptions.refuseWhenNoVisibleRows()) {
        throw new PolicyException(
            "this statement asks "
                + table.qualifiedName()
                + " for a '"
                + table.contradiction()
                + "' outside this principal's scope, so it can return no row, and "
                + "PrepareOptions.RefuseWhenNoVisibleRows asks for an error rather than an empty "
                + "result (docs/design/16-entitlements.md §3.12).");
      }
      EntitledTable.Builder entitled =
          EntitledTable.newBuilder()
              .setSchema(table.schema())
              .setTable(table.table())
              .setDescriptorHash(table.descriptorHash())
              .setRowPredicatePushed(result.pushedRowPredicates().contains(table.qualifiedName()))
              .setContradiction(!table.contradiction().isEmpty())
              .setContradictionColumn(table.contradiction())
              .setVisibility(visibility);
      // What this statement tested of the table without reading it (D261, §3): the audit observer's
      // own record of what was asked, since enumeration by repeated probes is the host's to limit.
      for (DisclosureMap.Tested tested : table.tested()) {
        entitled.addTested(
            chalk.planner.rpc.v1.TestedColumn.newBuilder()
                .setColumn(tested.column())
                .addAllShapes(tested.shapes()));
      }
      report.addTables(entitled);
    }

    return report.build();
  }

  /**
   * The oracle (§3.12, D207): what the policy resolved to, for a caller that asked. Built by the
   * pass that enforces it, so it cannot drift from what runs — which is the whole of its value.
   */
  private static EntitlementsExplain explain(PlannerPipeline.Result result) {
    EntitlementsExplain.Builder explain = EntitlementsExplain.newBuilder();
    for (PolicyExplain.Table table : result.explained()) {
      ExplainedTable.Builder explained =
          ExplainedTable.newBuilder()
              .setSchema(table.schema())
              .setTable(table.table())
              .setRowPredicate(table.rowPredicate())
              .setRowPredicatePushed(
                  result.pushedRowPredicates().contains(table.schema() + "." + table.table()))
              .addAllResidual(PolicyExplain.residual(result.physical(), table));
      for (PolicyExplain.Column column : table.columns()) {
        explained.addColumns(
            ExplainedColumn.newBuilder()
                .setColumn(column.name())
                .setDisclosure(column.disclosure())
                .setMask(column.mask())
                .setPlaceholder(column.placeholder())
                .setMinGroupSize(column.minGroupSize()));
      }
      for (PolicyExplain.Parent parent : table.parents()) {
        explained.addParents(
            chalk.planner.rpc.v1.ExplainedParent.newBuilder()
                .setSchema(parent.schema())
                .setTable(parent.table())
                .setColumn(parent.column())
                .setParentColumn(parent.parentColumn())
                .setElided(parent.elided())
                .setKeyDisclosure(parent.keyDisclosure()));
      }
      for (PolicyExplain.Path path : table.paths()) {
        chalk.planner.rpc.v1.ExplainedPath.Builder explainedPath =
            chalk.planner.rpc.v1.ExplainedPath.newBuilder()
                .setKind(path.kind())
                .setEndpointSchema(path.endpointSchema())
                .setEndpointTable(path.endpointTable())
                .setElided(path.elided())
                .setDropped(path.dropped())
                .setEndpointPredicate(path.endpointPredicate())
                .setKeyDisclosure(path.keyDisclosure());
        for (PolicyExplain.Step step : path.steps()) {
          explainedPath.addSteps(
              chalk.planner.rpc.v1.ExplainedStep.newBuilder()
                  .setSchema(step.schema())
                  .setTable(step.table())
                  .setFromColumn(step.fromColumn())
                  .setToColumn(step.toColumn())
                  .setToChild(step.toChild()));
        }
        explained.addPaths(explainedPath);
      }
      explain.addTables(explained);
    }
    return explain.build();
  }
}
