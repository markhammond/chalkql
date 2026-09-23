# ChalkQL — SQL, safely.

Federated SQL query planning and execution for .NET, powered by
[Apache Calcite](https://calcite.apache.org/). Query application-owned data and remote
SQL sources through one relational plan, with optional application-defined entitlements
enforced before execution.

[![ci](https://github.com/markhammond/chalkql/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/chalkql/actions/workflows/ci.yml)

Targets .NET 10. A JDK 21+ is required only when running the Calcite planner locally.

NuGet packages:

| Package                                                                                                                        | Licence    |                                                               |
| ------------------------------------------------------------------------------------------------------------------------------ | ---------- | ------------------------------------------------------------- |
| ChalkQL [![NuGet](https://img.shields.io/nuget/v/ChalkQL.svg)](https://www.nuget.org/packages/ChalkQL)                         | Apache-2.0 | engine, planner client, catalogue, execution and entitlements |
| ChalkQL.Sources [![NuGet](https://img.shields.io/nuget/v/ChalkQL.Sources.svg)](https://www.nuget.org/packages/ChalkQL.Sources) | Apache-2.0 | POCO, Akade.IndexedSet, ADO.NET, DuckDB and source conformance implementations |

The public packages use the `ChalkQL` name; assemblies and .NET namespaces retain
`Chalk.*`.

## Why this exists

The .NET ecosystem has comparatively little in the way of query-engine infrastructure.
ChalkQL fills that gap with a planner the application owns: one relational plan over the data an
application holds and the databases it reaches, optimised by Apache Calcite, executed by
vectorised operators inside the .NET host, and — when asked by the application — rewritten
before execution to enforce need-to-know access and tenant isolation across every source in the
plan.

## Batteries included

```
dotnet add package ChalkQL
dotnet add package ChalkQL.Sources
```

A local planner requires a JDK 21 or newer; the planner JAR itself is already embedded
in `Chalk.Client.dll`.

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

On macOS and Linux the local sidecar uses a Unix domain socket by default, so there is
no port to assign or configure.

## Bring your own data

ChalkQL introduces no persistence layer: data is queried on demand, wherever it resides.

Application-owned `IReadOnlyList<T>` collections can be exposed directly as relational
tables, including declared keys, collations, statistics and indexes. Remote sources
declare the operations they can perform; ChalkQL pushes work down when supported and
executes the remainder locally.

A single plan can therefore span:

* application-owned POCO collections;
* `Akade.IndexedSet` collections, with the indexes they were built with;
* ADO.NET databases;
* DuckDB;
* partitioned tables;
* custom sources implementing `ISourceRuntime`;
* cross-source joins between them.

SQL-defined, host-language and native functions may participate in the same plan, with
pushdown where the underlying source supports them.

## Hints for better planning

A prepared statement is planned once and executed many times, so the plan has to fit the
request rather than the statement:

* a `LIMIT` reaches the leaf that can stop early, which is costed for the rows the limit will
  pull rather than for its whole output — a hint to the source, never a bound;
* `LIMIT ?` and `OFFSET ?` are parameters like any other, read when the execution starts and
  pushed to a source in that source's own spelling;
* a prepare may say what it expects its parameters to be worth, and the planner estimates from
  those values instead of guessing — `PrepareAsync(sql, parameterValueHints)` — without the hint
  ever becoming a truth: the same plan runs whatever is bound later, and no hint reaches a log;
* a statement can be logged safely: with redaction on, every literal becomes a keyed pseudonym,
  a folded context value's pseudonym names the entry it came from, and a parameter written as
  `@name` reads as `@name`.

## Access control you can reason about.

ChalkQL’s entitlement layer is entirely optional. When used, policy is explicit and inspectable rather than reconstructed from views, predicates, ORMs or application code.

Entitlements may draw on application state, relationships, roles, resource scopes, or other domain-specific context to govern what a principal may access or derive:

* **Row and column access** — restrict which rows and columns a principal may access.
* **Value disclosure** — allow direct access, masked values, or testing the presence of a value without revealing it.
* **Tenant isolation** — constrain access to the appropriate tenant or resource scope, including through transitive relationships.
* **Aggregate disclosure** — permit approved statistical aggregates over protected values without granting direct access to those values.
* **Relationship-aware scopes** — resolve access through multiple declared relationships while preserving the scope that confines a grant; for example, a franchise owner may access their stores while an auditor accesses stores within their region.

## Choose your own topology

For development and co-located deployment, the simplest arrangement lets ChalkQL own a local JVM planner sidecar:

```csharp
await using var sidecar = await PlannerProcess.StartAsync();
var planner = sidecar.CreatePlanner();
```

The matching planner JAR is embedded in `Chalk.Client.dll`, materialised lazily into a content-addressed per-user cache the first time it is needed, and reused thereafter.

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

**A local planner still needs Java.** The JAR is bundled; the JVM is not. Install a
JDK 21 or newer, configure `JAVA_HOME`, or point `PlannerProcessOptions.JavaHome` at one.
A remotely deployed planner removes that requirement from the .NET host.

**The embedded planner writes to a cache when first used.** ChalkQL does not extract
anything merely because the assembly was loaded. `PlannerProcess.StartAsync()` performs
lazy materialisation. `PlannerProcessOptions.ArtifactCacheDirectory` or
`CHALK_PLANNER_CACHE` can redirect the cache for containers and locked-down hosts.

**Source capabilities are promises.** Pushdown is based on what a source declares it can
evaluate. The source conformance package exists to test those declarations against the
database rather than discovering disagreement in production.

**An in-process collection must not change under a running query.** POCO and Akade sources
keep one rule: no mutation overlaps an execution or a refresh. Mutate between requests, swap the
set behind a delegate, or use the transactional refresh, which gives each execution its own
snapshot.

**ChalkQL is currently read-only.** `SELECT` is supported; DML (future), DDL and transactions are
not.

**Not every correlated query shape is planned yet.** Some complex correlated or
`LATERAL` joins may currently be refused where Calcite cannot produce a plan ChalkQL is
prepared to execute safely.

## Documentation

The [guide](https://github.com/markhammond/chalkql/blob/main/docs/guide.md) covers
configuration, behaviour and extension points.

The [tutorial](https://github.com/markhammond/chalkql/blob/main/docs/tutorial.md) follows
one marketplace from application-owned tables through federation, functions,
entitlements, replanning and temporal streaming queries. Its published output is captured
from real executions and checked by the repository's tutorial script.

The full project README, architecture notes, samples and source are in the
[ChalkQL repository](https://github.com/markhammond/chalkql).

## Licence and attribution

ChalkQL is licensed under the Apache License 2.0.

Apache Calcite and its JVM dependencies are bundled into the planner artefact distributed
with `ChalkQL`. Their licences and attribution are recorded in
`THIRD-PARTY-NOTICES.txt`.

ChalkQL is an independent project and is not affiliated with or endorsed by the Apache
Software Foundation.
