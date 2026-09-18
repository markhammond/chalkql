# ChalkQL

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
| ChalkQL.Sources [![NuGet](https://img.shields.io/nuget/v/ChalkQL.Sources.svg)](https://www.nuget.org/packages/ChalkQL.Sources) | Apache-2.0 | POCO, ADO.NET, DuckDB and source conformance implementations  |

The public packages use the `ChalkQL` name; assemblies and .NET namespaces retain
`Chalk.*`.

## Why this exists

The .NET ecosystem has comparatively little in terms of query-engine infrastructure.
ChalkQL challenges this by applying zero-trust principles to SQL, rewriting queries to enforce need-to-know access, with tenant isolation across federated data sources. 
*Federated SQL query planning and optimisation for .NET, powered by <a href="https://calcite.apache.org">Apache Calcite</a>.*

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
* ADO.NET databases;
* DuckDB;
* partitioned tables;
* custom sources implementing `ISourceRuntime`;
* cross-source joins between them.

SQL-defined, host-language and native functions may participate in the same plan, with
pushdown where the underlying source supports them.

## Domain-specific entitlements.

Entirely optional and at your discretion. Entitlements are application-defined and may incorporate application state, relationships, roles, resource scopes, or other domain-specific policy, and can govern:

* **Rows and columns** — restrict which records and values a principal may access.
* **Tenant isolation** — constrain access to the appropriate tenant or resource scope, including transitive relationships.
* **Aggregate disclosure** — permit approved statistical aggregates over protected values without granting direct access to those values.
* **Multi-dimensional resource scopes** — resolve access through different relationships to the same resource; for example, a franchise owner may access their stores while an auditor accesses stores within their region.


## Choose your own topology

For development and co-located deployment, the simplest form owns a local JVM sidecar:

```csharp
await using var sidecar = await PlannerProcess.StartAsync();
var planner = sidecar.CreatePlanner();
```

The matching planner JAR is embedded in `Chalk.Client.dll`. It is materialised lazily
into a content-addressed per-user cache the first time a local planner is required and
reused thereafter.

Resolution is explicit before falling back to the embedded artefact:

```
PlannerProcessOptions.JarPath
    ↓
CHALK_PLANNER_JAR
    ↓
embedded planner → per-user content-addressed cache
```

An explicitly configured path that does not exist is an error; it is never silently
replaced by the embedded planner.

Production deployments may instead run the same planner independently on another host
and connect to it over gRPC. In that topology no local planner process is started and
the embedded JAR is never materialised.

`PlannerArtifact` exposes the matching embedded artefact for deployment tooling without
requiring callers to know its manifest-resource name.

## Well-mannered scalability

A host may create multiple `ChalkEngine` contexts while planner sidecars are shared
independently of them.

Engine instances and planners have a many-to-many relationship. Planning workloads can
be partitioned by application-defined instance name and governed through priorities,
deadlines, time-slicing and compute budgets, while vectorised query execution remains
inside the .NET host.

The JVM process boundary is intentional: it preserves Apache Calcite's planner
extensibility while isolating its memory and resource usage from the application.

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
