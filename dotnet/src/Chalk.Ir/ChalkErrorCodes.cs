namespace Chalk;

/// <summary>
/// The rules a ChalkQL error can break, one for each remedy a host is given, as the glossary,
/// <c>docs/errors.md</c>, names and explains them. Each is a name, carried in
/// <see cref="ChalkViolation.Code"/> and <see cref="ChalkException.Code"/>, at the end of the message
/// in brackets, and on the wire from the planner.
/// </summary>
/// <remarks>
/// <para>
/// A code is compared as text, ordinally: <c>failure.Code == ChalkErrorCodes.DuplicateName</c>, or a
/// <c>case ChalkErrorCodes.DuplicateName:</c> in a switch.
/// </para>
/// <para>
/// The set grows. A planner newer than this client can send a code with no constant here; the
/// error carries it as it was sent, and the glossary of that version explains it.
/// </para>
/// </remarks>
public static class ChalkErrorCodes
{
    // Catalog declarations.

    /// <summary>A name or id that must be given is empty.</summary>
    public const string EmptyName = nameof(EmptyName);

    /// <summary>A name is used twice where it must be unique.</summary>
    public const string DuplicateName = nameof(DuplicateName);

    /// <summary>
    /// A declaration names a schema, table, column, function, parameter, index or source the
    /// catalog does not hold.
    /// </summary>
    public const string UnknownName = nameof(UnknownName);

    /// <summary>A descriptor's column index is past the end of its table.</summary>
    public const string ColumnIndexOutOfRange = nameof(ColumnIndexOutOfRange);

    /// <summary>A name the planner keeps for its own markers, or one that would shadow a built-in function.</summary>
    public const string ReservedName = nameof(ReservedName);

    /// <summary>A number outside the range its setting allows.</summary>
    public const string ValueOutOfRange = nameof(ValueOutOfRange);

    /// <summary>Two columns a declaration pairs have different types.</summary>
    public const string TypeMismatch = nameof(TypeMismatch);

    /// <summary>A type that cannot be used where it is declared or read.</summary>
    public const string UnsupportedType = nameof(UnsupportedType);

    /// <summary>A join a declaration relies on is not on a declared unique key, so it could multiply rows.</summary>
    public const string NotAUniqueKey = nameof(NotAUniqueKey);

    /// <summary>A declaration lacks a part it needs.</summary>
    public const string IncompleteDeclaration = nameof(IncompleteDeclaration);

    /// <summary>An index, collation or covering set that cannot work as declared.</summary>
    public const string InvalidIndex = nameof(InvalidIndex);

    /// <summary>A foreign key that does not pair its columns with its parent's key.</summary>
    public const string InvalidForeignKey = nameof(InvalidForeignKey);

    /// <summary>Partitions that do not match the table they partition.</summary>
    public const string InvalidPartitioning = nameof(InvalidPartitioning);

    /// <summary>A user function whose kind, signature, body or properties disagree.</summary>
    public const string InvalidFunction = nameof(InvalidFunction);

    /// <summary>
    /// Source capabilities or a dialect profile that contradict each other or the source's query
    /// language.
    /// </summary>
    public const string InvalidCapabilities = nameof(InvalidCapabilities);

    /// <summary>Join-policy limits or pair rules that cannot apply.</summary>
    public const string InvalidJoinPolicy = nameof(InvalidJoinPolicy);

    /// <summary>
    /// The sources of one catalog declare different security domains, or a zone is written with
    /// white space around it.
    /// </summary>
    public const string InvalidZone = nameof(InvalidZone);

    // Entitlement declarations.

    /// <summary>An entitlement whose rules, masks, tests or defaults cannot take effect as written.</summary>
    public const string InvalidEntitlement = nameof(InvalidEntitlement);

    /// <summary>A mask, placeholder or default would read a protected column.</summary>
    public const string MaskReadsProtectedColumn = nameof(MaskReadsProtectedColumn);

    /// <summary>RequireEntitlements is on and a table carries no entitlement and is not marked public.</summary>
    public const string MissingEntitlement = nameof(MissingEntitlement);

    // Tenancy policy.

    /// <summary>The tenancy policy's own declarations disagree with each other or with the catalog.</summary>
    public const string InvalidPolicy = nameof(InvalidPolicy);

    /// <summary>A path does not lead from a row to where its kind is held.</summary>
    public const string InvalidTenancyPath = nameof(InvalidTenancyPath);

    /// <summary>Derived visibility returns to a table already on its chain.</summary>
    public const string VisibilityCycle = nameof(VisibilityCycle);

    /// <summary>A bridge key a rule or realm protects, which the mechanism must read raw.</summary>
    public const string ProtectedBridgeKey = nameof(ProtectedBridgeKey);

    /// <summary>An access rule or realm that names what it may not.</summary>
    public const string InvalidAccessRule = nameof(InvalidAccessRule);

    /// <summary>A declared combination resolves on a table where no one route answers it.</summary>
    public const string UnansweredCombination = nameof(UnansweredCombination);

    /// <summary>A dimension the table cannot be confined by.</summary>
    public const string ConfinementUnavailable = nameof(ConfinementUnavailable);

    // Principals and grants.

    /// <summary>
    /// A grant names an undeclared role, scope or kind, or is confined as the policy does not
    /// allow.
    /// </summary>
    public const string InvalidGrant = nameof(InvalidGrant);

    /// <summary>A grant whose scopes are not a combination the policy declares for its role.</summary>
    public const string UndeclaredCombination = nameof(UndeclaredCombination);

    // Request context and parameters.

    /// <summary>A statement over an entitled catalog prepared or run without an execution context.</summary>
    public const string ContextRequired = nameof(ContextRequired);

    /// <summary>An execution context the statement cannot use.</summary>
    public const string InvalidContext = nameof(InvalidContext);

    /// <summary>
    /// A parameter value that cannot be bound exactly, or a hint for a parameter the statement does
    /// not have.
    /// </summary>
    public const string ParameterBinding = nameof(ParameterBinding);

    // Statements.

    /// <summary>The statement does not parse.</summary>
    public const string SqlSyntax = nameof(SqlSyntax);

    /// <summary>The statement parses but names what does not exist, or combines types SQL does not allow.</summary>
    public const string SqlValidation = nameof(SqlValidation);

    /// <summary>A statement that is not a query.</summary>
    public const string UnsupportedStatement = nameof(UnsupportedStatement);

    /// <summary>Valid SQL ChalkQL does not plan.</summary>
    public const string UnsupportedSql = nameof(UnsupportedSql);

    /// <summary>A function, operator or aggregate ChalkQL does not implement for these arguments.</summary>
    public const string UnsupportedFunction = nameof(UnsupportedFunction);

    /// <summary>A cast between types ChalkQL does not convert.</summary>
    public const string UnsupportedCast = nameof(UnsupportedCast);

    /// <summary>A LIST or COMPOSITE used where values are ordered or compared.</summary>
    public const string IncomparableType = nameof(IncomparableType);

    /// <summary>An argument that must be a literal or a parameter.</summary>
    public const string NonConstantArgument = nameof(NonConstantArgument);

    /// <summary>A join across sources that would fetch more rows than the join policy allows.</summary>
    public const string CrossSourceJoinRefused = nameof(CrossSourceJoinRefused);

    /// <summary>The planner's governor ended its search with no complete plan.</summary>
    public const string PlanningAborted = nameof(PlanningAborted);

    /// <summary>A plan nested deeper than the configured limit.</summary>
    public const string PlanTooDeep = nameof(PlanTooDeep);

    // Entitlement refusals.

    /// <summary>A column the principal may only aggregate, used some other way.</summary>
    public const string PopulationOnly = nameof(PopulationOnly);

    /// <summary>
    /// A statistical column read under a window, or a declared unique key of its table compared or
    /// grouped by.
    /// </summary>
    public const string Statistical = nameof(Statistical);

    /// <summary>A column that discloses nothing, under a redaction policy that refuses it.</summary>
    public const string Redacted = nameof(Redacted);

    /// <summary>A star, or a ROW over a table's columns, under a star policy that refuses it.</summary>
    public const string Star = nameof(Star);

    /// <summary>A statement that can return no row, under RefuseWhenNoVisibleRows.</summary>
    public const string NoVisibleRows = nameof(NoVisibleRows);

    /// <summary>A row predicate declared PUSHDOWN_REQUIRED that the plan would evaluate locally.</summary>
    public const string PushdownRequired = nameof(PushdownRequired);

    /// <summary>An entitlement this request's binding time cannot apply.</summary>
    public const string EntitlementBinding = nameof(EntitlementBinding);

    /// <summary>A disclosure column whose name the statement already produces.</summary>
    public const string DisclosureNameCollision = nameof(DisclosureNameCollision);

    // Execution.

    /// <summary>The plan names a source the engine does not hold.</summary>
    public const string UnknownSource = nameof(UnknownSource);

    /// <summary>A plan operator this executor or source does not run.</summary>
    public const string UnsupportedOperator = nameof(UnsupportedOperator);

    /// <summary>A user function given, or returning, a value of a type it may not take or return.</summary>
    public const string UserFunctionContract = nameof(UserFunctionContract);

    /// <summary>A plan calls a user function the live catalog does not declare.</summary>
    public const string UserFunctionUnavailable = nameof(UserFunctionUnavailable);

    /// <summary>Text that is not valid UTF-8, from a function or a source.</summary>
    public const string InvalidUtf8 = nameof(InvalidUtf8);

    /// <summary>A query needed more memory than its arena allows.</summary>
    public const string MemoryBudgetExceeded = nameof(MemoryBudgetExceeded);

    /// <summary>A batch read after it was released.</summary>
    public const string BatchLifetime = nameof(BatchLifetime);

    /// <summary>An operator failed while the query ran; the inner exception says why.</summary>
    public const string ExecutionFailed = nameof(ExecutionFailed);

    // Sources.

    /// <summary>A source answered outside its contract.</summary>
    public const string SourceContract = nameof(SourceContract);

    /// <summary>A refresh that does not fit its source.</summary>
    public const string SourceRefresh = nameof(SourceRefresh);

    /// <summary>A declared ordering, unique key or foreign key the data does not hold.</summary>
    public const string DeclarationNotHeld = nameof(DeclarationNotHeld);

    /// <summary>The source's own error; the inner exception carries it.</summary>
    public const string SourceFailed = nameof(SourceFailed);

    /// <summary>A source did not answer in time.</summary>
    public const string SourceTimeout = nameof(SourceTimeout);

    /// <summary>A plan prepared against a catalog that has since changed.</summary>
    public const string StalePlan = nameof(StalePlan);

    /// <summary>A source failed its conformance check.</summary>
    public const string ConformanceFailed = nameof(ConformanceFailed);

    // Engine and planner.

    /// <summary>The planner process could not be reached, or did not answer in time.</summary>
    public const string PlannerUnavailable = nameof(PlannerUnavailable);

    /// <summary>
    /// The planner and this client share no plan format, or a plan uses a node this client does not
    /// know.
    /// </summary>
    public const string IrVersionMismatch = nameof(IrVersionMismatch);

    /// <summary>Planner or engine options that cannot be honoured.</summary>
    public const string InvalidConfiguration = nameof(InvalidConfiguration);

    /// <summary>A request the planner will not act on as written.</summary>
    public const string InvalidRequest = nameof(InvalidRequest);

    /// <summary>A request extension no handler is registered for.</summary>
    public const string UnknownExtension = nameof(UnknownExtension);

    /// <summary>A recorded planner holds no plan for the statement.</summary>
    public const string RecordedPlanMissing = nameof(RecordedPlanMissing);

    /// <summary>The caller cancelled planning.</summary>
    public const string PlanningCancelled = nameof(PlanningCancelled);

    /// <summary>The client refused a plan whose reads or disclosures contradict the catalog's entitlements.</summary>
    public const string PlanBreaksEntitlements = nameof(PlanBreaksEntitlements);

    /// <summary>A guard ChalkQL holds itself to. Reaching one is a bug, to be reported with the statement.</summary>
    public const string Internal = nameof(Internal);
}
