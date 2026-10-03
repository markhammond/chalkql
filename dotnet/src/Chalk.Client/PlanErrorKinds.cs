namespace Chalk.Client;

/// <summary>
/// The kinds of problem the planner names, coarser than a code and finer than a gRPC status: what
/// <see cref="PlanningException.Kind"/> and a planner's <see cref="ChalkViolation.Kind"/> hold.
/// </summary>
/// <remarks>
/// A kind is compared as text, ordinally. A planner newer than this client can name a kind with no
/// constant here; the error carries it as it was sent.
/// </remarks>
public static class PlanErrorKinds
{
    /// <summary>The statement does not parse.</summary>
    public const string Parse = nameof(Parse);

    /// <summary>The statement parses but names what does not exist, or combines types SQL does not allow.</summary>
    public const string Validation = nameof(Validation);

    /// <summary>Valid SQL the planner cannot express in the IR, or cannot physically plan.</summary>
    public const string Unsupported = nameof(Unsupported);

    /// <summary>A context id no catalog is registered under. Retired from the request path.</summary>
    public const string UnknownContext = nameof(UnknownContext);

    /// <summary>A catalog registered at an epoch other than the one the request names.</summary>
    public const string EpochMismatch = nameof(EpochMismatch);

    /// <summary>This client speaks an IR version the planner cannot serve.</summary>
    public const string IrVersion = nameof(IrVersion);

    /// <summary>A catalog the planner refuses to register.</summary>
    public const string InvalidCatalog = nameof(InvalidCatalog);

    /// <summary>A guard the planner holds itself to. Reaching one is a bug, to be reported with the statement.</summary>
    public const string Internal = nameof(Internal);

    /// <summary>
    /// Valid SQL the entitlements refuse: the planner could plan it, and declines to. Raised as an
    /// <see cref="Chalk.Entitlements.EntitlementException"/>.
    /// </summary>
    public const string Policy = nameof(Policy);

    /// <summary>
    /// A request the planner will not act on as written, such as an extension no handler is
    /// registered for, which is never ignored.
    /// </summary>
    public const string InvalidRequest = nameof(InvalidRequest);

    /// <summary>
    /// A search the request's own options ended before there was a plan.
    /// <see cref="PlanningException.PlanningState"/> says what had been measured.
    /// </summary>
    public const string PlanningAborted = nameof(PlanningAborted);

    /// <summary>
    /// A catalog version the planner does not hold, evicted or lost to a restart. The engine
    /// registers it again and retries once, so a host sees this only when that fails.
    /// </summary>
    public const string UnknownCatalogVersion = nameof(UnknownCatalogVersion);
}
