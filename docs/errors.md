# Error codes

Every exception ChalkQL raises on purpose names the rules it breaks, each by a code:
`ChalkException.Violations` lists them, and `ChalkException.Code` is the first one's.
`ContextRequiredException` and `PlanningCancelledException` derive from framework types, so each
carries a `Code` of its own. The message ends with the code in brackets:

```text
Invalid catalog at schemas[0] (sales).tables[1] (orders): table name 'orders' is used twice in
schema 'sales' [DuplicateName]
```

A code names the rule the error meets and the remedy that goes with it, and the message names
the case. A host can branch on the code, or log it without the table, column and principal names
a message carries. A code is text, compared ordinally, and `ChalkErrorCodes` has a constant for
each one below; a planner newer than the client can send a code it has no constant for, which
the error carries as it was sent. ChalkQL stops at the first rule it finds broken, so an error
carries one violation today. One raised through a constructor that takes no code carries none,
and its `Code` is null.

Arguments a call gets wrong in its own shape, such as a null where a value is required, are the
framework's `ArgumentException` and carry no code.

## Catalog declarations

Raised when a catalog is built or registered (`CatalogValidationException`), before any
statement is planned. The message names the place in the catalog, as a path such as `schemas[0]
(sales).tables[2] (orders).columns[1]`.

### EmptyName

A schema, table, column, function, parameter, index or source id that must be given was left
empty.

**What to do:** Give it a name.

### DuplicateName

Two schemas, tables, columns, functions, parameters, indexes, foreign keys or fields share a
name where names must be unique. Names of schemas and fields match ignoring case.

**What to do:** Rename one of them.

### UnknownName

A declaration names a schema, table, column, function, parameter, index, source, role, scope or
kind that the catalog or the policy does not hold, often through a misspelling.

**What to do:** Correct the name, or declare what it names first.

### ColumnIndexOutOfRange

A descriptor refers to a column by its position, and the position is past the end of the table.
This comes from a descriptor built in code rather than from a builder.

**What to do:** Correct the descriptor's column position.

### ReservedName

A name begins with a prefix the planner keeps for its own markers, or a user function has the
name of a built-in function it would shadow.

**What to do:** Rename it.

### ValueOutOfRange

A number outside the range its setting allows: a negative row count, a cost that is not finite,
a DECIMAL precision above 38, a join-policy limit below zero. The message gives the range.

**What to do:** Use a value in the range the message gives.

### TypeMismatch

Two columns that a declaration pairs have different types: a foreign key and its parent key, a
path step's two columns, a partition's columns and the partitioned table's, a type declared
beside the one inferred from a CLR member.

**What to do:** Make the two types agree.

### UnsupportedType

A type that cannot be used where it is declared or read: a LIST of LISTs, a COMPOSITE inside
another, a COMPOSITE column on a source that is not in-process, a provider or Arrow type ChalkQL
has no type for.

**What to do:** Use a type ChalkQL supports there; the message says which are.

### NotAUniqueKey

A join that a declaration relies on — an association, a path step, a `Through` parent — is not
on a declared unique key of the table it reaches, so it could match a row more than once and
multiply rows.

**What to do:** Declare the unique key, or join on one.

### IncompleteDeclaration

A declaration lacks a part it needs: a catalog with no schema, a table with no column, a key or
index with no column, an index kind or sort direction left unspecified.

**What to do:** Supply the missing part.

### InvalidIndex

An index, collation or covering set that cannot work as declared: a prefix index over more than
one column or over a column that is not text, a covering set that leaves out a key column, a
built-in index over a collection with no positions.

**What to do:** Correct the index declaration.

### InvalidForeignKey

A foreign key that does not pair its columns with its parent's key: no columns, or a different
number of columns from the parent key.

**What to do:** Correct the foreign key.

### InvalidPartitioning

A partitioned table whose partitions do not match it: no partitions, a partition that says
neither its value nor its range, a partition with other columns than the table.

**What to do:** Correct the partitions.

### InvalidFunction

A user function whose declaration disagrees with itself: a table function with no row type, a
property that only an aggregate has on a scalar function, a default on a required parameter, a
SQL body that does not parse or names a parameter it does not declare.

**What to do:** Correct the function's declaration.

### InvalidCapabilities

A source's capabilities or dialect profile contradict each other or the source's query language:
pushable work on a source that takes no queries, OFFSET without LIMIT, a case-insensitive
collation beside LIKE that matches code points, a dialect, conformance level or library the
planner does not know.

**What to do:** Correct the capabilities or the dialect profile.

### InvalidJoinPolicy

A join policy whose limits are negative, or a pair rule that prefers a strategy it does not
allow.

**What to do:** Correct the join policy.

### InvalidZone

The sources of one catalog declare different security domains (zones), or only some of them
declare one, or a zone is written with white space around it. One catalog is one zone.

**What to do:** Build one engine per zone over that zone's sources, and declare the same zone on
every source of a catalog.

## Entitlement declarations

Raised when a catalog that carries entitlements is registered. The message names the table, the
column and the rule.

### InvalidEntitlement

An entitlement whose rules cannot take effect as written: a mask no rule reaches, a rule that
discloses Masked with no mask, an aggregate allow-list naming a function that is not a
population aggregate, an entitlement that does not parse or type-check. Raised at registration,
and by the planner when it applies the entitlement.

**What to do:** Correct the entitlement; the message names the table, the column and the rule.

### MaskReadsProtectedColumn

A mask, placeholder or default reads a column that is itself protected. A mask is evaluated over
the raw row, so it would disclose what the other column's rules withhold.

**What to do:** Read an unprotected column in the mask, or protect less.

### MissingEntitlement

`CatalogOptions.RequireEntitlements` is on and a table carries no entitlement and is not marked
public.

**What to do:** Give the table an entitlement, or mark it public.

## Tenancy policy

Raised when a tenancy policy is compiled against a catalog.

### InvalidPolicy

The tenancy policy's own declarations disagree with each other or with the catalog: it speaks
about a source the catalog does not hold, a combination names a role twice or no role, a handle
comes from another policy, a kind is declared both as a tenancy and a subject kind.

**What to do:** Correct the policy.

### InvalidTenancyPath

A path does not lead from a row to where its kind is held: it has no step, goes up where it may
not, returns to its own table, ends somewhere other than its endpoint, or uses a step with no
foreign key or association to follow.

**What to do:** Correct the path's steps or its endpoint.

### VisibilityCycle

A table's visibility derives, through `Through` or a path, from a chain that returns to a table
already on it. A row's visibility cannot derive from itself.

**What to do:** Break the cycle.

### ProtectedBridgeKey

A bridge table's key column, which the mechanism reads raw and never discloses, is protected by
a rule, a realm or the table's default.

**What to do:** Leave the bridge key unprotected, or reach the endpoint another way.

### InvalidAccessRule

An access rule or realm that names what it may not: a column or realm the table does not hold,
`Roles.Visible` beside other grantees, a role that does not resolve on the table, a verdict and
a shape that cannot go together.

**What to do:** Correct the access rule.

### UnansweredCombination

A combination the policy declares for a role resolves on a table, but its kinds are reached
along different routes, so no one route answers the whole combination.

**What to do:** Declare the kinds where one route holds them all, or declare the combination for
the tables that can answer it.

### ConfinementUnavailable

A grant would be confined along a kind that the table cannot confine by: the kind is not on the
table's own row or along the same path.

**What to do:** Declare the kind on the table, or hold the grant unconfined.

## Principals and grants

Raised when a principal's grants are bound to a policy, before the statement is planned.

### InvalidGrant

A grant names a role, scope or kind the policy does not declare, is global where the policy does
not permit global grants, or is confined along its own kind or twice along one.

**What to do:** Correct the grant builder.

### UndeclaredCombination

A grant's scopes are not one of the combinations the policy declares for its role. A policy that
declares combinations compiles those and nothing else, so a grant of any other shape is refused
rather than left to reach less, or more, than was meant.

**What to do:** Build the grant with the scopes its role's combination requires, or declare the
combination.

## Request context and parameters

Raised when a request's context or a statement's parameters are bound. A refused value is never
quoted: the message names what was bound and the CLR type bound to it.

### ContextRequired

A statement over a catalog that carries entitlements was prepared or run without an execution
context, or a plan prepared for a shape was run without the values it needs.

**What to do:** Bind the principal's context when preparing, or its values when executing.

### InvalidContext

A context the statement cannot use: a value with no type, a name bound twice, a list used as a
table, a relation in an IN list, a name the statement reads that the context does not bind, rows
with the wrong number of values.

**What to do:** Correct the context; the message names the value.

### ParameterBinding

A value bound to a parameter or a context value that its type does not hold exactly (a fraction
to an integer, text to a number, a value out of range), a NULL bound to a parameter declared NOT
NULL, a malformed LIKE pattern, a converter that failed, or a list bound where no list fits. The
message names what was bound and its CLR type, never the value.

**What to do:** Bind a value of the parameter's type.

## Statements

Raised when a statement is prepared (`PlanningException`). The message carries the position in
the SQL where the planner knew one.

### SqlSyntax

The statement does not parse.

**What to do:** Correct the SQL at the position the message gives.

### SqlValidation

The statement parses, but names a table or column that does not exist, combines types SQL does
not allow, or writes a LIKE pattern that is malformed under its escape.

**What to do:** Correct the SQL at the position the message gives.

### UnsupportedStatement

A statement that is not a query: one that changes a schema, a transaction or a session.

**What to do:** Run it against the source directly.

### UnsupportedSql

Valid SQL that ChalkQL does not plan: a join type, grouping sets, a LATERAL form it cannot
decorrelate, a table function used outside its form.

**What to do:** Rewrite the statement; the message says what is not planned.

### UnsupportedFunction

A function, operator or aggregate that ChalkQL does not implement for these arguments.

**What to do:** Use a function it implements; the message lists them where it can.

### UnsupportedCast

A cast between two types that ChalkQL does not convert.

**What to do:** Cast through a type ChalkQL supports.

### IncomparableType

A LIST or a COMPOSITE used where values are ordered or compared: ORDER BY, GROUP BY, a join key,
an index key, a set operation that compares rows.

**What to do:** Order or compare by a scalar, such as one of a composite's fields.

### NonConstantArgument

An argument that has to be a literal or a parameter is computed: a LIKE pattern or escape, a
percentile fraction, a LISTAGG separator, a window's size or slide.

**What to do:** Pass a literal or a parameter.

### CrossSourceJoinRefused

A join across two sources would fetch more rows into the engine than the join policy allows, or
would put one source under the inner side of a nested-loop join.

**What to do:** Narrow the join, or raise the join policy's limit.

### PlanningAborted

The planning budget this request set ran out before the planner had a complete plan.
`PlanningException.PlanningState` says what had been measured.

**What to do:** Simplify the statement, or allow a larger budget.

### PlanTooDeep

A plan nests deeper than the client's limit: a statement with sub-queries or joins nested by the
hundred.

**What to do:** Simplify the statement, or raise `PlanNestingLimit` on a host whose threads have
the stack for it.

## Entitlement refusals

Raised when a statement is prepared and the entitlements decline it (`EntitlementException`).
`Refusals` has one refusal for each rule the statement breaks, and `Refusal` is the first: each
carries its code, the matching `EntitlementRefusal.Reason`, the table, the column and the use,
and the result the statement would have had.

### PopulationOnly

A column the principal may only aggregate was used some other way: returned as a value,
compared, grouped or sorted by. `Refusal.Permitted` names the aggregates it allows.

**What to do:** Aggregate the column with one of the permitted aggregates.

### Statistical

A statistical column was read under a window function, or a declared unique key of its table was
compared or grouped by.

**What to do:** Read the column only through aggregates over whole groups.

### Redacted

A column that discloses nothing to this principal was named or surfaced by a star, under a
redaction policy that refuses rather than returning a placeholder.

**What to do:** Leave the column out, or prepare under a redaction policy that returns a
placeholder.

### Star

A star, or a ROW over a table's columns, under a star policy that refuses it while the catalog
carries entitlements.

**What to do:** Name the columns.

### NoVisibleRows

The statement can return no row for this principal, and `RefuseWhenNoVisibleRows` asks for a
refusal rather than an empty answer.

**What to do:** Bind a principal whose grants reach the table, or accept the empty answer.

### PushdownRequired

A table's row predicate is declared `PUSHDOWN_REQUIRED`, and the plan would evaluate it in the
engine rather than in the source.

**What to do:** Let the source evaluate the predicate, or relax the requirement.

### EntitlementBinding

An entitlement this request's binding time cannot apply, such as a membership of a bound
relation under a negation when the context is bound at execution.

**What to do:** Bind the context when preparing, or restate the entitlement.

### DisclosureNameCollision

`IncludeDisclosureColumns` would add a disclosure column whose name the statement already
produces.

**What to do:** Rename the statement's column.

## Execution

Raised while a prepared statement runs.

### UnknownSource

The plan names a source the engine does not hold.

**What to do:** Register the source with the engine.

### UnsupportedOperator

A plan operator this executor, or a source, does not run.

**What to do:** Report it with the statement.

### UserFunctionContract

A user function was given, or returned, a value of a type its implementation may not take or
return: a CLR type outside the ones its tier takes, a table function's row member of another
type than its column.

**What to do:** Change the function's signature or its declared types.

### UserFunctionUnavailable

A plan calls a user function that the live catalog does not declare, or that nothing is
registered to implement.

**What to do:** Prepare the statement against the catalog that declares the function, and
register its implementation.

### InvalidUtf8

Bytes that were about to become a STRING value are not valid UTF-8, from a POCO member, a source
or a user function.

**What to do:** Return valid UTF-8, or declare the member BINARY if it is not text.

### MemoryBudgetExceeded

A query needed more memory than its execution arena allows.

**What to do:** Narrow the query, or raise the arena's budget.

### BatchLifetime

A batch's column was read after the producer had moved on to the next batch.

**What to do:** Copy what has to outlive the batch before reading the next one.

### ExecutionFailed

An operator failed while the query ran. The inner exception says why.

**What to do:** See the inner exception.

## Sources

Raised by a source, or by the engine about a source.

### SourceContract

A source answered outside its contract: a batch with other columns than were asked for, a NULL
in a NOT NULL column, a DECIMAL outside its declared precision, pushed work it did not declare,
a table or index it does not hold.

**What to do:** Correct the source, or its declaration.

### SourceRefresh

A refresh does not fit its source: it names a table twice, carries rows of another type, changes
the row shape, or comes from an editor that returned nothing.

**What to do:** Correct the refresh.

### DeclarationNotHeld

A declared ordering, unique key or foreign key does not hold over the data: rows out of the
declared order, a key that repeats, a foreign key with no parent row.

**What to do:** Correct the data or the declaration, or turn off the check where the host
guarantees it another way.

### SourceFailed

The source raised an error of its own while running a query or a scan. The inner exception is
the provider's.

**What to do:** See the inner exception.

### SourceTimeout

A source did not answer within its `QueryTimeout`.

**What to do:** Retry, or raise the source's timeout.

### StalePlan

A plan was prepared against a catalog that has since changed: a table it reads has a different
shape now.

**What to do:** Prepare the statement again.

### ConformanceFailed

A source failed its conformance check against its own descriptor.

**What to do:** Correct the source; the report says which probe failed and why.

## Engine and planner

Raised about the engine, the planner process or the request itself.

### PlannerUnavailable

The planner process could not be reached, did not answer within the deadline, or answered as
something other than a planner.

**What to do:** Check that the planner process is running and reachable.

### IrVersionMismatch

The planner and this client share no plan format, or a plan uses a node this client does not
know.

**What to do:** Use matching versions of the ChalkQL packages and the planner.

### InvalidConfiguration

Planner or engine options that cannot be honoured: a limit below one, two transports at once, an
option with no value.

**What to do:** Correct the options.

### InvalidRequest

A request the planner will not act on as written: statistics with no engine instance, a
redaction with no salt where one is needed.

**What to do:** Correct the request.

### UnknownExtension

A request carries an extension that no handler is registered for, carries one twice, or carries
one that does not parse. An extension is never ignored, since a policy silently dropped is a
policy not enforced.

**What to do:** Register the extension's handler, or drop the extension.

### RecordedPlanMissing

A recorded planner holds no plan for the statement.

**What to do:** Record the plan; the message carries the command.

### PlanningCancelled

The host cancelled the prepare. `PlanningCancelledException.PlanningState` says how far planning
had got.

**What to do:** Nothing, unless the cancellation was not meant.

### PlanBreaksEntitlements

The client checked a plan against the catalog's own entitlements before running it and found a
read or a disclosure that contradicts them. The plan does not run.

**What to do:** Report it with the statement: the planner returned a plan that breaks the
policy.

### Internal

A guard ChalkQL holds itself to was reached: a plan that breaks an invariant of the plan format,
a rewrite that left something it should not have. Reaching one is a bug.

**What to do:** Report it with the statement and, where the message carries one, the invariant
or correlation id.
