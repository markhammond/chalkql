package chalk.planner.rpc;

import chalk.planner.UnsupportedFeatureException;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.rpc.v1.PlanError;
import chalk.planner.rpc.v1.PlanErrorKind;
import io.grpc.Metadata;
import io.grpc.Status;
import io.grpc.StatusRuntimeException;
import java.util.UUID;
import org.apache.calcite.plan.RelOptPlanner;
import org.apache.calcite.runtime.CalciteContextException;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.tools.ValidationException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * Exception → gRPC status plus a {@link PlanError} in the {@code chalk-plan-error-bin} trailer
 * (docs/design/03-planner.md §7). The trailer rather than {@code google.rpc.Status} details, so
 * neither side needs the googleapis protos (D25).
 */
public final class PlanErrors {
  private static final Logger LOG = LoggerFactory.getLogger(PlanErrors.class);

  /** The trailer every failed Plan or RegisterCatalog call carries. */
  public static final Metadata.Key<byte[]> TRAILER =
      Metadata.Key.of("chalk-plan-error-bin", Metadata.BINARY_BYTE_MARSHALLER);

  private PlanErrors() {}

  /** Maps a planning failure to the status and trailer the client expects. */
  public static StatusRuntimeException toStatus(Throwable error) {
    if (error instanceof SqlParseException parse) {
      return build(Status.INVALID_ARGUMENT, PlanErrorKind.PLAN_ERROR_KIND_PARSE, message(parse), parse.getPos());
    }
    if (error instanceof ValidationException validation) {
      Throwable cause = validation.getCause() == null ? validation : validation.getCause();
      if (cause instanceof CalciteContextException context) {
        return build(
            Status.INVALID_ARGUMENT,
            PlanErrorKind.PLAN_ERROR_KIND_VALIDATION,
            message(context),
            context.getPosLine(),
            context.getPosColumn(),
            context.getEndPosLine(),
            context.getEndPosColumn());
      }
      return build(
          Status.INVALID_ARGUMENT, PlanErrorKind.PLAN_ERROR_KIND_VALIDATION, message(cause), null);
    }
    if (error instanceof CalciteContextException context) {
      return build(
          Status.INVALID_ARGUMENT,
          PlanErrorKind.PLAN_ERROR_KIND_VALIDATION,
          message(context),
          context.getPosLine(),
          context.getPosColumn(),
          context.getEndPosLine(),
          context.getEndPosColumn());
    }
    // Valid SQL the entitlements refuse (D143). PERMISSION_DENIED rather than INVALID_ARGUMENT: the
    // statement is well formed and the planner could express it perfectly well; it is declining to,
    // and a client that retries with different values will be declined again.
    if (error instanceof chalk.planner.entitlement.PolicyException policy) {
      return build(
          Status.PERMISSION_DENIED, PlanErrorKind.PLAN_ERROR_KIND_POLICY, policy.getMessage(), null);
    }
    // A statement naming one of the planner's own markers (D223). INVALID_REQUEST rather than PARSE:
    // the text parses perfectly well and the planner is declining to plan it, because the name would
    // shadow a rewrite rather than resolve to anything the host declared.
    if (error instanceof chalk.planner.ReservedNames.ReservedNameException reserved) {
      return build(
          Status.INVALID_ARGUMENT,
          PlanErrorKind.PLAN_ERROR_KIND_INVALID_REQUEST,
          reserved.getMessage(),
          null);
    }
    // An extension this planner has no handler for, or one whose bytes are not what its type URL
    // names (step 26c, D212). Refused rather than ignored: a policy silently dropped is a policy not
    // enforced.
    if (error instanceof chalk.planner.ext.PlanExtensionRegistry.UnknownExtensionException unknown) {
      return build(
          Status.INVALID_ARGUMENT,
          PlanErrorKind.PLAN_ERROR_KIND_INVALID_REQUEST,
          unknown.getMessage(),
          null);
    }
    // A search this request's own options ended before there was a plan (D235). RESOURCE_EXHAUSTED
    // rather than UNIMPLEMENTED: the statement is fine, the rules are fine, and a caller that
    // retries with a larger budget gets a plan. The state travels in the trailer beside the message.
    if (error instanceof chalk.planner.plan.PlannerPipeline.PlanningAbortedException aborted) {
      return build(
          Status.RESOURCE_EXHAUSTED,
          PlanErrorKind.PLAN_ERROR_KIND_PLANNING_ABORTED,
          aborted.getMessage(),
          0,
          0,
          0,
          0,
          PlannerServiceImpl.planningState(aborted.state(), 0L, 0L));
    }
    if (error instanceof UnsupportedFeatureException unsupported) {
      return build(
          Status.UNIMPLEMENTED, PlanErrorKind.PLAN_ERROR_KIND_UNSUPPORTED, unsupported.getMessage(), null);
    }
    if (error instanceof RelOptPlanner.CannotPlanException cannotPlan) {
      return build(
          Status.UNIMPLEMENTED,
          PlanErrorKind.PLAN_ERROR_KIND_UNSUPPORTED,
          "This query cannot be physically planned by this milestone's rule set. " + cannotPlan.getMessage(),
          null);
    }
    // A shape version this planner does not hold (D271 (b), (e)). FAILED_PRECONDITION, like the two
    // it replaces, but what a client does with it is recover: register the version the message names
    // and retry once. Eviction and restart both arrive here, and neither is an error a host sees.
    if (error instanceof UnknownCatalogVersionException version) {
      return build(
          Status.FAILED_PRECONDITION,
          PlanErrorKind.PLAN_ERROR_KIND_UNKNOWN_CATALOG_VERSION,
          version.getMessage(),
          null);
    }
    if (error instanceof UnknownContextException unknown) {
      return build(
          Status.FAILED_PRECONDITION, PlanErrorKind.PLAN_ERROR_KIND_UNKNOWN_CONTEXT, unknown.getMessage(), null);
    }
    if (error instanceof EpochMismatchException mismatch) {
      return build(
          Status.FAILED_PRECONDITION, PlanErrorKind.PLAN_ERROR_KIND_EPOCH_MISMATCH, mismatch.getMessage(), null);
    }
    if (error instanceof IrVersionException version) {
      return build(
          Status.FAILED_PRECONDITION, PlanErrorKind.PLAN_ERROR_KIND_IR_VERSION, version.getMessage(), null);
    }
    if (error instanceof InvalidCatalogException invalid) {
      return build(
          Status.INVALID_ARGUMENT, PlanErrorKind.PLAN_ERROR_KIND_INVALID_CATALOG, invalid.getMessage(), null);
    }
    if (error instanceof IllegalArgumentException illegal) {
      return build(
          Status.INVALID_ARGUMENT, PlanErrorKind.PLAN_ERROR_KIND_VALIDATION, illegal.getMessage(), null);
    }

    // Anything else is ours to fix. The client gets a correlation id it can quote; the stack trace
    // stays in the log where it belongs.
    String correlationId = UUID.randomUUID().toString();
    LOG.error("Internal planner error [{}]", correlationId, error);
    return build(
        Status.INTERNAL,
        PlanErrorKind.PLAN_ERROR_KIND_INTERNAL,
        error.getClass().getName() + ": " + error.getMessage() + " [correlation id " + correlationId + "]",
        null);
  }

  private static StatusRuntimeException build(
      Status status, PlanErrorKind kind, String message, SqlParserPos position) {
    if (position == null) {
      return build(status, kind, message, 0, 0, 0, 0);
    }
    return build(
        status,
        kind,
        message,
        position.getLineNum(),
        position.getColumnNum(),
        position.getEndLineNum(),
        position.getEndColumnNum());
  }

  private static StatusRuntimeException build(
      Status status, PlanErrorKind kind, String message, int line, int column, int endLine, int endColumn) {
    return build(status, kind, message, line, column, endLine, endColumn, null);
  }

  private static StatusRuntimeException build(
      Status status,
      PlanErrorKind kind,
      String message,
      int line,
      int column,
      int endLine,
      int endColumn,
      chalk.planner.rpc.v1.@org.checkerframework.checker.nullness.qual.Nullable PlanningState state) {
    String text = message == null ? kind.name() : message;
    PlanError.Builder builder =
        PlanError.newBuilder()
            .setKind(kind)
            .setMessage(text)
            .setLine(line)
            .setColumn(column)
            .setEndLine(endLine)
            .setEndColumn(endColumn);
    if (state != null) {
      builder.setPlanningState(state);
    }
    PlanError error = builder.build();
    Metadata trailers = new Metadata();
    trailers.put(TRAILER, error.toByteArray());
    return status.withDescription(text).asRuntimeException(trailers);
  }

  private static String message(Throwable error) {
    return error.getMessage() == null ? error.toString() : error.getMessage();
  }

  /**
   * The (instance_id, shape_version) a request names is not one this planner holds, or a delta's
   * base is not (D271 (b), design 44 §5). Memory policy and never correctness: the client registers
   * the version named here and retries once.
   */
  public static final class UnknownCatalogVersionException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private final String instanceId;
    private final String version;

    public UnknownCatalogVersionException(String instanceId, String version) {
      this(instanceId, version, "the shape a plan names. Register it and retry.");
    }

    public UnknownCatalogVersionException(String instanceId, String version, String what) {
      super(
          "This planner holds no catalog version '"
              + version
              + "' for engine instance '"
              + instanceId
              + "': "
              + what);
      this.instanceId = instanceId;
      this.version = version;
    }

    /** The engine instance the missing version belongs to. */
    public String instanceId() {
      return instanceId;
    }

    /** The version that is missing, which is what the client registers before retrying. */
    public String version() {
      return version;
    }
  }

  /** The (context_id) named by a request is not registered. Retired from the request path by D271. */
  public static final class UnknownContextException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    public UnknownContextException(String contextId) {
      super(
          "No catalog is registered under context id '"
              + contextId
              + "'. Call RegisterCatalog before Plan.");
    }
  }

  /** The catalog is registered but at a different epoch than the request names. */
  public static final class EpochMismatchException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    public EpochMismatchException(String contextId, long requested, long registered) {
      super(
          "Catalog '"
              + contextId
              + "' is registered at epoch "
              + registered
              + " but the request names epoch "
              + requested
              + ". Re-register the catalog, or re-read it and retry.");
    }
  }

  /** The client speaks an IR version this planner cannot serve. */
  public static final class IrVersionException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    public IrVersionException(int clientVersion, int min, int max) {
      super(
          "This planner serves IR versions "
              + min
              + ".."
              + max
              + "; the client speaks version "
              + clientVersion
              + ".");
    }
  }
}
