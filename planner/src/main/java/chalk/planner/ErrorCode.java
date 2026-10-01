package chalk.planner;

/**
 * The rule an error meets, one for each remedy a host is given, as the glossary
 * ({@code docs/errors.md}) names it. A violation carries it by {@link #wireName() name}, so a
 * rule is added here and in the glossary, with no change to the protocol, and a client keeps a
 * name it does not know as it was sent.
 */
public enum ErrorCode {
  // Catalog declarations.

  /** A name or id that must be given is empty. */
  EMPTY_NAME("EmptyName"),
  /** A name is used twice where it must be unique. */
  DUPLICATE_NAME("DuplicateName"),
  /**
   * A declaration names a schema, table, column, function, parameter, index or source the catalog
   * does not hold.
   */
  UNKNOWN_NAME("UnknownName"),
  /** A descriptor's column index is past the end of its table. */
  COLUMN_INDEX_OUT_OF_RANGE("ColumnIndexOutOfRange"),
  /** A name the planner keeps for its own markers, or one that would shadow a built-in function. */
  RESERVED_NAME("ReservedName"),
  /** A number outside the range its setting allows. */
  VALUE_OUT_OF_RANGE("ValueOutOfRange"),
  /** Two columns a declaration pairs have different types. */
  TYPE_MISMATCH("TypeMismatch"),
  /** A type that cannot be used where it is declared or read. */
  UNSUPPORTED_TYPE("UnsupportedType"),
  /** A join a declaration relies on is not on a declared unique key, so it could multiply rows. */
  NOT_A_UNIQUE_KEY("NotAUniqueKey"),
  /** A declaration lacks a part it needs. */
  INCOMPLETE_DECLARATION("IncompleteDeclaration"),
  /** An index, collation or covering set that cannot work as declared. */
  INVALID_INDEX("InvalidIndex"),
  /** A foreign key that does not pair its columns with its parent's key. */
  INVALID_FOREIGN_KEY("InvalidForeignKey"),
  /** Partitions that do not match the table they partition. */
  INVALID_PARTITIONING("InvalidPartitioning"),
  /** A user function whose kind, signature, body or properties disagree. */
  INVALID_FUNCTION("InvalidFunction"),
  /**
   * Source capabilities or a dialect profile that contradict each other or the source's query
   * language.
   */
  INVALID_CAPABILITIES("InvalidCapabilities"),
  /** Join-policy limits or pair rules that cannot apply. */
  INVALID_JOIN_POLICY("InvalidJoinPolicy"),
  /**
   * The sources of one catalog declare different security domains, or a zone is written with white
   * space around it.
   */
  INVALID_ZONE("InvalidZone"),

  // Entitlement declarations.

  /** An entitlement whose rules, masks, tests or defaults cannot take effect as written. */
  INVALID_ENTITLEMENT("InvalidEntitlement"),
  /** A mask, placeholder or default would read a protected column. */
  MASK_READS_PROTECTED_COLUMN("MaskReadsProtectedColumn"),
  /** RequireEntitlements is on and a table carries no entitlement and is not marked public. */
  MISSING_ENTITLEMENT("MissingEntitlement"),

  // Tenancy policy.

  /** The tenancy policy's own declarations disagree with each other or with the catalog. */
  INVALID_POLICY("InvalidPolicy"),
  /** A path does not lead from a row to where its kind is held. */
  INVALID_TENANCY_PATH("InvalidTenancyPath"),
  /** Derived visibility returns to a table already on its chain. */
  VISIBILITY_CYCLE("VisibilityCycle"),
  /** A bridge key a rule or realm protects, which the mechanism must read raw. */
  PROTECTED_BRIDGE_KEY("ProtectedBridgeKey"),
  /** An access rule or realm that names what it may not. */
  INVALID_ACCESS_RULE("InvalidAccessRule"),
  /** A declared combination resolves on a table where no one route answers it. */
  UNANSWERED_COMBINATION("UnansweredCombination"),
  /** A dimension the table cannot be confined by. */
  CONFINEMENT_UNAVAILABLE("ConfinementUnavailable"),

  // Principals and grants.

  /**
   * A grant names an undeclared role, scope or kind, or is confined as the policy does not allow.
   */
  INVALID_GRANT("InvalidGrant"),
  /** A grant whose scopes are not a combination the policy declares for its role. */
  UNDECLARED_COMBINATION("UndeclaredCombination"),

  // Request context and parameters.

  /** A statement over an entitled catalog prepared or run without an execution context. */
  CONTEXT_REQUIRED("ContextRequired"),
  /** An execution context the statement cannot use. */
  INVALID_CONTEXT("InvalidContext"),
  /**
   * A parameter value that cannot be bound exactly, or a hint for a parameter the statement does
   * not have.
   */
  PARAMETER_BINDING("ParameterBinding"),

  // Statements.

  /** The statement does not parse. */
  SQL_SYNTAX("SqlSyntax"),
  /** The statement parses but names what does not exist, or combines types SQL does not allow. */
  SQL_VALIDATION("SqlValidation"),
  /** A statement that is not a query. */
  UNSUPPORTED_STATEMENT("UnsupportedStatement"),
  /** Valid SQL ChalkQL does not plan. */
  UNSUPPORTED_SQL("UnsupportedSql"),
  /** A function, operator or aggregate ChalkQL does not implement for these arguments. */
  UNSUPPORTED_FUNCTION("UnsupportedFunction"),
  /** A cast between types ChalkQL does not convert. */
  UNSUPPORTED_CAST("UnsupportedCast"),
  /** A LIST or COMPOSITE used where values are ordered or compared. */
  INCOMPARABLE_TYPE("IncomparableType"),
  /** An argument that must be a literal or a parameter. */
  NON_CONSTANT_ARGUMENT("NonConstantArgument"),
  /** A join across sources that would fetch more rows than the join policy allows. */
  CROSS_SOURCE_JOIN_REFUSED("CrossSourceJoinRefused"),
  /** The planner's governor ended its search with no complete plan. */
  PLANNING_ABORTED("PlanningAborted"),
  /** A plan nested deeper than the configured limit. */
  PLAN_TOO_DEEP("PlanTooDeep"),

  // Entitlement refusals.

  /** A column the principal may only aggregate, used some other way. */
  POPULATION_ONLY("PopulationOnly"),
  /**
   * A statistical column read under a window, or a declared unique key of its table compared or
   * grouped by.
   */
  STATISTICAL("Statistical"),
  /** A column that discloses nothing, under a redaction policy that refuses it. */
  REDACTED("Redacted"),
  /** A star, or a ROW over a table's columns, under a star policy that refuses it. */
  STAR("Star"),
  /** A statement that can return no row, under RefuseWhenNoVisibleRows. */
  NO_VISIBLE_ROWS("NoVisibleRows"),
  /** A row predicate declared PUSHDOWN_REQUIRED that the plan would evaluate locally. */
  PUSHDOWN_REQUIRED("PushdownRequired"),
  /** An entitlement this request's binding time cannot apply. */
  ENTITLEMENT_BINDING("EntitlementBinding"),
  /** A disclosure column whose name the statement already produces. */
  DISCLOSURE_NAME_COLLISION("DisclosureNameCollision"),

  // Execution.

  /** The plan names a source the engine does not hold. */
  UNKNOWN_SOURCE("UnknownSource"),
  /** A plan operator this executor or source does not run. */
  UNSUPPORTED_OPERATOR("UnsupportedOperator"),
  /** A user function given, or returning, a value of a type it may not take or return. */
  USER_FUNCTION_CONTRACT("UserFunctionContract"),
  /** A plan calls a user function the live catalog does not declare. */
  USER_FUNCTION_UNAVAILABLE("UserFunctionUnavailable"),
  /** Text that is not valid UTF-8, from a function or a source. */
  INVALID_UTF8("InvalidUtf8"),
  /** A query needed more memory than its arena allows. */
  MEMORY_BUDGET_EXCEEDED("MemoryBudgetExceeded"),
  /** A batch read after it was released. */
  BATCH_LIFETIME("BatchLifetime"),
  /** An operator failed while the query ran; the inner exception says why. */
  EXECUTION_FAILED("ExecutionFailed"),

  // Sources.

  /** A source answered outside its contract. */
  SOURCE_CONTRACT("SourceContract"),
  /** A refresh that does not fit its source. */
  SOURCE_REFRESH("SourceRefresh"),
  /** A declared ordering, unique key or foreign key the data does not hold. */
  DECLARATION_NOT_HELD("DeclarationNotHeld"),
  /** The source's own error; the inner exception carries it. */
  SOURCE_FAILED("SourceFailed"),
  /** A source did not answer in time. */
  SOURCE_TIMEOUT("SourceTimeout"),
  /** A plan prepared against a catalog that has since changed. */
  STALE_PLAN("StalePlan"),
  /** A source failed its conformance check. */
  CONFORMANCE_FAILED("ConformanceFailed"),

  // Engine and planner.

  /** The planner process could not be reached, or did not answer in time. */
  PLANNER_UNAVAILABLE("PlannerUnavailable"),
  /**
   * The planner and this client share no plan format, or a plan uses a node this client does not
   * know.
   */
  IR_VERSION_MISMATCH("IrVersionMismatch"),
  /** Planner or engine options that cannot be honoured. */
  INVALID_CONFIGURATION("InvalidConfiguration"),
  /** A request the planner will not act on as written. */
  INVALID_REQUEST("InvalidRequest"),
  /** A request extension no handler is registered for. */
  UNKNOWN_EXTENSION("UnknownExtension"),
  /** A recorded planner holds no plan for the statement. */
  RECORDED_PLAN_MISSING("RecordedPlanMissing"),
  /** The caller cancelled planning. */
  PLANNING_CANCELLED("PlanningCancelled"),
  /** The client refused a plan whose reads or disclosures contradict the catalog's entitlements. */
  PLAN_BREAKS_ENTITLEMENTS("PlanBreaksEntitlements"),
  /** A guard ChalkQL holds itself to. Reaching one is a bug, to be reported with the statement. */
  INTERNAL("Internal");

  private final String wireName;

  ErrorCode(String wireName) {
    this.wireName = wireName;
  }

  /** The code's name, as the glossary, the wire and the end of a host's message spell it. */
  public String wireName() {
    return wireName;
  }
}
