# ChalkQL — SQL, safely.

ChalkQL applies **zero-trust principles to SQL**, rewriting queries to enforce need-to-know access and tenant isolation across federated data sources. *Federated SQL query planning and optimisation for .NET, powered by [Apache Calcite](https://calcite.apache.org/).*

Targets .NET 10. A JDK 21+ is required only when running the Apache Calcite planner locally.

NuGet packages:

| Package | Licence | |
| --- | --- | --- |
| ChalkQL [![NuGet](https://img.shields.io/nuget/v/ChalkQL.svg)](https://www.nuget.org/packages/ChalkQL) | Apache-2.0 | engine, planner client, catalogue, execution and entitlements |
| ChalkQL.Sources [![NuGet](https://img.shields.io/nuget/v/ChalkQL.Sources.svg)](https://www.nuget.org/packages/ChalkQL.Sources) | Apache-2.0 | POCO, Akade.IndexedSet, ADO.NET, DuckDB and source conformance implementations |

The public packages use the `ChalkQL` name; assemblies and .NET namespaces retain `Chalk.*`.

## Why this exists

The .NET ecosystem has comparatively little query-engine infrastructure.

ChalkQL provides a planner the application owns: one relational plan over data held by the application and databases it reaches, optimised by Apache Calcite and executed by vectorised operators inside the .NET host.

When requested by the application, that plan can also be rewritten before execution to enforce need-to-know access and tenant isolation consistently across its sources.

## Batteries included

```text
dotnet add package ChalkQL
dotnet add package ChalkQL.Sources
```

A local planner requires JDK 21 or newer. The matching planner JAR is embedded in `Chalk.Client.dll`.

```csharp
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources.Poco;

public sealed record UsdRate(string Currency, DateOnly Ts, double Rate);

var rows = new List<UsdRate>
{
    new("EUR", new DateOnly(2026, 1, 3), 1.17),
    new("GBP", new DateOnly(2026, 1, 3), 1.34),
};

var rates = new PocoSourceBuilder("mem")
    .AddTable("usd_rates", rows, t => t
        .OrderedBy(r => r.Ts)
        .ThenBy(r => r.Currency)
        .UniqueKey(r => r.Ts, r => r.Currency))
    .Build();

await using var sidecar = await PlannerProcess.StartAsync();

await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
{
    ContextId = "demo",
    Sources = [rates],
    Planner = sidecar.CreatePlanner(),
});

var query = await engine.PrepareAsync(
    "SELECT currency, ts, rate FROM usd_rates WHERE currency = ?");

await using var execution = await engine.ExecuteAsync(query, ["EUR"]);
```

On macOS and Linux the local sidecar uses a Unix domain socket by default, so there is no port to assign or configure.

Results arrive as Apache Arrow record batches.

## Federation for (almost) everyone

ChalkQL queries data on demand, wherever it resides, without introducing a persistence layer.

Operations are pushed down where supported; the remainder executes locally. A single plan can therefore span heterogeneous remote sources and application-provided virtual tables:

* Cross-source joins, subject to data-source capabilities
* Virtual tables backed by application data
* Indexed in-memory virtual tables
* POCO and `Akade.IndexedSet` collections
* ADO.NET and DuckDB sources
* Custom sources through `ISourceRuntime`

In-process collections can declare keys, collations, statistics and indexes, while remote sources declare the operations they can perform.

## Sovereignty at the seams

Federation creates a subtle trust boundary: a query spanning independently governed data sources may inadvertently disclose information from one source to another simply through the work pushed down during execution.

Where data sources are physically segregated by sovereign zone, ChalkQL favours separate catalogues, with the host selecting the appropriate catalogue for each request. A single query therefore cannot cross a sovereign boundary by construction.

Work that genuinely spans sovereign zones remains deliberate and visible: execute separate statements against the relevant engines and combine their results explicitly in application code.

## Bring your own functions

SQL-defined, host-language and native functions can participate in the same plan, with pushdown where eligible.

Host-language functions are ordinary .NET delegates and can use familiar CLR types such as `decimal`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset`, `TimeSpan` and `Guid`.

Functions and aggregates may also return **composite types**:

```csharp
public readonly record struct Classification(
    Utf8String Category,
    double Confidence);
```

SQL addresses their fields directly:

```sql
SELECT id,
       classify_transaction(description, amount).category   AS category,
       classify_transaction(description, amount).confidence AS confidence
FROM transactions
WHERE classify_transaction(description, amount).confidence > 0.8
```

Repeated immutable or stable expressions within one execution step are computed once per batch, so the classifier above runs once per row rather than once for every field reference. `Volatile()` functions remain evaluated once per occurrence.

The same record may be exposed as a column of an in-process POCO or `AkadeSource` table. Composite results return to the host as Arrow struct columns and can be read into the corresponding record type with `GetComposite<T>`, `TryGetComposite<T>` or `ReadComposites<T>`.

Composite types are deliberately flat: fields are scalar, and recursive or nested composite types are not supported.

They also provide a foundation for future async functions, allowing a remote invocation to return a structured result in a single call.

## Zero-cost abstractions, rules

ChalkQL favours abstractions whose cost disappears when they are not used, and which stay close to the metal when they are.

STRING and BINARY values staged in Apache Arrow buffers can be accessed as `ReadOnlySpan<byte>` rather than decoded or copied per row.

```csharp
ReadOnlySpan<byte> symbol = batch.Column(0).GetUtf8(row);
```

The same bytes can pass straight into host-language functions and aggregates, or be used for allocation-free lookup against string-keyed collections.

```csharp
registry.AddScalar<ReadOnlySpan<byte>, long>(
    "byte_length",
    value => value.Length);
```

Decode or copy only when the value needs to outlive the batch.

## Hints for better planning

A prepared statement is planned once and may be executed many times, so a good plan sometimes depends on knowing the shape of the expected request.

A `LIMIT` is propagated towards a leaf that can stop early, allowing the planner to cost the rows expected to be consumed rather than necessarily the source's complete output.

`LIMIT ?` and `OFFSET ?` remain ordinary execution parameters and can be pushed to a source using that source's SQL spelling.

Representative parameter values may also be supplied when preparing a statement:

```csharp
PrepareAsync(sql, parameterValueHints)
```

Hints influence selectivity and costing only. They never constrain execution: the same prepared plan accepts whatever values are subsequently bound, and hinted values are not written to logs.

## Privacy-aware diagnostics

SQL can be logged without retaining its literal values. With redaction enabled, literals become typed, stable pseudonyms while the shape of the statement remains useful for traces, slow-query logs and diagnostics:

```sql
-- host SQL
WHERE holder_email = 'jane.doe@example.com'
  AND date_of_birth = DATE '1987-04-23'

-- PreparedQuery.RedactedSql
WHERE "holder_email" = /*REDACTED-a571576b:CHAR*/
  AND "date_of_birth" = /*REDACTED-c50c7437:DATE*/
```

**Redaction does not depend on successful parsing.** If a statement contains a syntax error, ChalkQL falls back to token-level redaction so recognised literals are still removed:

```sql
SELCT account_id
FROM accounts
WHERE holder_email = /*REDACTED-c94b5d85:CHAR*/
  AND national_id = /*REDACTED-30702dd9:CHAR*/
```

Parameterised values remain parameters and require no redaction. The same pseudonyms are used in redacted plan text, while the guide covers the full redaction contract.

## Access control you can reason about

ChalkQL's entitlement layer is entirely optional. When used, policy is explicit and inspectable rather than reconstructed from views, predicates, ORMs or application code.

Entitlements may draw on application state, relationships, roles, resource scopes or other domain-specific context to govern what a principal may access or derive:

* **Rows and columns** — restrict which rows and columns a principal may access.
* **Value disclosure** — allow direct access, masking, or testing the presence of a value without revealing it.
* **Tenant isolation** — constrain access to the appropriate tenant or resource scope, including through transitive relationships.
* **Aggregate disclosure** — permit approved statistical aggregates over protected values without granting direct access, including application aggregates declared population-safe with `.Population()`.
* **Multi-dimensional resource scopes** — express access along more than one independent ownership or tenancy dimension.

Relationship and resource-scope constraints can only restrict what is visible, confining access on a strictly need-to-know basis. The sole exception is an explicit “read what you own” policy, permitting the designated resource owner to retain access as a fail-safe.

## Security, meet first principles

Treating SQL as a _mathematically closed language_ is foundational to ChalkQL’s security architecture: every permitted derivation of protected data must remain expressible within the relational plan that ChalkQL can inspect and rewrite.

Entitlement policy is embedded into that plan before optimisation and execution. Structural enforcement at the relational level means access controls remain part of the query through joins, subqueries, CTEs and aggregation, rather than depending on each query author to reproduce the right predicates.

## Choose your own topology

For development and co-located deployment, the simplest arrangement lets ChalkQL own a local JVM planner sidecar:

```csharp
await using var sidecar = await PlannerProcess.StartAsync();
var planner = sidecar.CreatePlanner();
```

The matching planner JAR is embedded in `Chalk.Client.dll`, materialised lazily into a content-addressed per-user cache when first needed, and reused thereafter.

Planner resolution is explicit before falling back to the embedded artefact:

```text
PlannerProcessOptions.JarPath
    ↓
CHALK_PLANNER_JAR
    ↓
embedded planner → per-user content-addressed cache
```

An explicitly configured path that does not exist is an error; it is never silently replaced by the embedded planner.

Production deployments may instead run the same planner independently — including on another host — and connect over gRPC. In that topology no local planner process is started and the embedded JAR is never materialised. `PlannerArtifact` exposes the matching embedded artefact for deployment tooling without requiring callers to know its manifest-resource name.

A host may create multiple `ChalkEngine` contexts while planner sidecars are shared independently of them. Engines and planners have a many-to-many relationship: planning workloads can be partitioned by application-defined instance name and governed through priorities, deadlines, time-slicing and compute budgets, while vectorised query execution remains inside the .NET host.

The JVM process boundary is intentional. It preserves Apache Calcite's planner extensibility while isolating planner memory and resource usage from the application.

## Things that will bite you

**A local planner still needs Java.** The JAR is bundled; the JVM is not. Install JDK 21 or newer, configure `JAVA_HOME`, or point `PlannerProcessOptions.JavaHome` at one. A remotely deployed planner removes that requirement from the .NET host.

**The embedded planner writes to a cache when first used.** ChalkQL does not extract anything merely because the assembly was loaded. `PlannerProcess.StartAsync()` performs lazy materialisation. `PlannerProcessOptions.ArtifactCacheDirectory` or `CHALK_PLANNER_CACHE` can redirect the cache for containers and locked-down hosts.

**Source capabilities are promises.** Pushdown depends on what a source declares it can evaluate. The source conformance package exists to test those declarations against the database rather than discovering disagreement in production.

**An in-process collection must not change under a running query.** POCO and Akade sources permit no mutation that overlaps an execution or refresh. Mutate between requests, swap the collection behind a delegate, or use transactional refresh so each execution retains its own snapshot.

**Text is lent, not given.** `GetUtf8` and span-valued STRING or BINARY function arguments refer to memory whose lifetime is owned by the current batch or call. Copy anything that must survive longer.

**Composite types are deliberately limited.** They are flat, have no whole-value ordering or equality, and currently originate only from client functions and in-process table columns. Use their scalar fields for grouping, ordering, comparison, joins and indexes.

**Rounding follows SQL type semantics .** `ROUND` rounds the value represented by its input type. Exact numeric values use decimal rounding semantics; approximate numeric values may produce different results because their represented value need not equal the corresponding decimal value. ChalkQL is consistent with SQL Server and DuckDB; PostgreSQL declines to offer `ROUND(DOUBLE, scale)`

**ChalkQL is currently read-only.** `SELECT` is supported; DML, DDL and transactions are not.

**Not every correlated query shape is planned yet.** Some complex correlated or `LATERAL` joins may be refused where Calcite cannot produce a plan ChalkQL is prepared to execute safely.

## Documentation

The [guide](https://github.com/markhammond/chalkql/blob/main/docs/guide.md) covers configuration, behaviour and extension points.

The [tutorial](https://github.com/markhammond/chalkql/blob/main/docs/tutorial.md) follows one marketplace from application-owned tables through federation, functions, entitlements, replanning and temporal streaming queries. Its published output is captured from real executions and checked by the repository's tutorial script.

The full project README, architecture notes, samples and source are in the [ChalkQL repository](https://github.com/markhammond/chalkql).

## Licence and attribution

ChalkQL is licensed under the Apache License 2.0.

Apache Calcite and its JVM dependencies are bundled into the planner artefact distributed with `ChalkQL`. Their licences and attribution are recorded in `THIRD-PARTY-NOTICES.txt`. The planner's statement driver is derived from Apache Calcite's own `PlannerImpl`, and the repository's `NOTICE` records it.

ChalkQL is an independent project and is not affiliated with or endorsed by the Apache Software Foundation.