# ChalkQL — SQL, safely.

[![NuGet](https://img.shields.io/nuget/v/ChalkQL.svg)](https://www.nuget.org/packages/ChalkQL) [![ci](https://github.com/markhammond/chalkql/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/chalkql/actions/workflows/ci.yml)

ChalkQL applies zero-trust principles to SQL, rewriting queries to enforce need-to-know access and tenant isolation across federated data sources. *Federated SQL query planning and optimisation for .NET, powered by [Apache Calcite](https://calcite.apache.org).*

Targets .NET 10. JDK 21+ required for the Apache Calcite planner.

## Democratise access to your data with confidence.

The objective is simple: just write the SQL you mean and let ChalkQL enforce fine-grained authorisation. 

Applications and agents increasingly need to query data they do not completely own, across sources with different capabilities and trust boundaries. Access control is too often entangled with individual queries, views, ORMs, or application code — conflating query intent with what a principal is permitted to know.

### Who is this for, anyway?

ChalkQL is most useful where ***who may know what*** matters, or where the data needed to answer a question does not live neatly in one place.

Database row-level security is often exactly the right answer when access can be expressed cleanly within one database and its schema. ChalkQL addresses the less tidy case: authorisation follows relationships the original schema was not designed around, different principals reach the same data through different scopes, disclosure rules extend beyond rows, or the _correct_ answer spans several sources of truth.

It is intended for applications that need to:

* expose SQL or analytical capability across trust boundaries without granting unrestricted access to the underlying data;
* compartmentalise data by tenant, role, relationship or resource scope, including transitive and multi-dimensional relationships;
* query across fragmented sources of truth — databases, application-owned state, embedded data and services — without first consolidating everything into one database;
* keep application-defined authorisation independent of individual queries and of the physical source in which data happens to reside;
* evolve access rules as the domain changes, rather than requiring the original storage model to have anticipated every future boundary.

If one trusted application talks to one database and that database's native access controls already express everything required, ChalkQL's **entitlement layer** may add little. The less tidy middle ground is where it becomes challenging: the architecture has evolved, the data is already distributed, boundaries matter, and replacing every source with the theoretically perfect model is neither necessary nor practical.

There’s more to it, of course. But if battling these constraints sounds familiar, ChalkQL may be for you.

### _Live streaming queries the ~~hard~~ easy way._

The [tutorial](docs/tutorial.md) develops this step by step, culminating in a live inventory view combining streaming state, temporal joins and tenancy-aware access control — using idiomatic SQL throughout.

### Access control you can reason about.

ChalkQL separates application-defined entitlements from individual queries and physical sources. That makes the access model explicit: entitlements can be inspected and reasoned about without reconstructing them from views, predicates, ORMs or application code.

Entitlements may draw on application state, relationships, roles, resource scopes, or other domain-specific context to govern what a principal may access or derive:

* **Row and column access** — restrict which rows and columns a principal may access.
* **Value disclosure** — allow direct access, masked values, or testing the presence of a value without revealing it.
* **Tenancy** — constrain access to the appropriate tenant or resource scope, including transitive relationships.
* **Aggregate disclosure** — allow approved statistical aggregates over restricted values without direct access.
* **Multiple resource scopes** — resolve access through different relationships to the same resource; for example, a franchise owner may access their stores while an auditor accesses stores within their region.

### Federation for (almost) everyone.

ChalkQL brings Apache Calcite's federated relational query planning and optimisation to .NET applications. Operations are pushed down when supported by the underlying source, with ChalkQL’s embedded vectorised execution engine handling the remainder locally. This allows a single plan to span heterogeneous remote sources and application-provided virtual tables.

### Bring your own data.

ChalkQL introduces no persistence layer: data is queried on demand, wherever it resides.

* Cross-source joins, subject to data-source capabilities.
* Partial binding and incremental query optimisation.
* Virtual tables backed by application data.
* Indexed in-memory virtual tables.
* SQL-defined, host-language, and native UDFs, with pushdown where supported.
* Host-controlled resource usage during query execution.

### Security, meet first principles.

Treating SQL as a _mathematically closed language_ is foundational to ChalkQL’s security architecture. ChalkQL rewrites the relational plan to embed access-control policies before optimisation and execution. Structural enforcement at the query-plan level prevents access controls from being bypassed through joins, subqueries, CTEs, and aggregation.

## Choose your own topology

ChalkQL is predominantly an embedded **.NET library**. A host application may instantiate any number of `ChalkEngine` contexts, each with an application-defined instance name. Engines and planner sidecars have a many-to-many relationship, with planning workloads partitioned by instance and governed through priorities, deadlines, time-slicing, and compute budgets.

```text
┌─ your .NET application ─────────────┐           N : M          ┌─ planner JVM sidecars ─┐
│ ChalkEngine × N · app-defined names │ ◀──────────────────────▶ │   Apache Calcite × M   │
└─────────────────────────────────────┘   planning · scheduling  └────────────────────────┘
```

Apache Calcite query planning and optimisation are provided by lightweight JVM sidecars, while ChalkQL’s vectorised execution engine remains within the .NET host. The diagram below shows one path through that topology.

```text
      your app · .NET                                  planner sidecar · JVM 21
           │ SQL + parameters                          on the prepare path only
  ┌────────▼───────────────┐       prepare query       ┌──────────────────────┐
  │ ChalkEngine            │────── SQL + catalog ─────▶│ Apache Calcite:      │
  │  instance name         │                           │ parse → validate →   │
  │   <application-defined>│◀──── plan IR + digest ────│ optimise → plan IR   │
  │  catalog   declarations│     gRPC — UDS or TCP     └──────────────────────┘
  │  policy    entitlements│
  │  binding   params, @ctx│
  │────────────────────────│
  │  vectorised execution  │
  │  over Apache Arrow,    │──▶ your app reads Arrow RecordBatch + Stats
  │  in a pooled arena     │
  └───────────┬────────────┘
              │ scan · index lookup · pushdown (SQL or IR)
              │ nothing is pushed that a source has not declared
    ┌─────────┴───────────┬───────────────────────┐
    │                     │                       │
┌───▼──────────────┐ ┌────▼───────────────┐ ┌─────▼──────────────────┐
│ local source     │ │ remote source      │ │ remote source          │
│ POCO collections │ │ ADO.NET — SQLite,  │ │ DuckDB — native data   │
│ Chalk scans them │ │ PostgreSQL, …      │ │ chunks into the arena  │
└──────────────────┘ └────────────────────┘ └────────────────────────┘
  … or your own: ISourceRuntime is the extension point, one partitioned
  table may fan out over several, and a join may span all of them at once
```

The host retains control of application lifecycle and execution policy, while Calcite underpins relational planning and optimisation.

### Process isolation, as a feature.

Apache Calcite has helped catalyse a Cambrian explosion in query-engine development, while the .NET ecosystem has been comparatively underserved. Chalk adopts a symbiotic architecture to unite these ecosystems: Calcite runs in a sidecar, preserving planner extensibility while ring-fencing its memory from the host process.

By delegating parsing, validation, decorrelation, and optimisation to Calcite, ChalkQL provides sophisticated query planning alongside embedded vectorised execution — without imposing an opinionated deployment model on the host application.


## Beyond access control

A handful of open-source projects overlap with parts of ChalkQL’s capability set, though the comparison is not exhaustive: BetweenRows focuses on policy-enforced SQL proxying, SQE combines DataFusion query execution with fine-grained access control, and AccessFlow provides broader data access governance and proxying.

| Capability                                             |         **ChalkQL**          |       BetweenRows        |        SQE           |     AccessFlow    |
|--------------------------------------------------------|:----------------------------:|:------------------------:|:--------------------:|:-----------------:|
| `SELECT` queries                                       |              ✅¹             |            ✅             |         ✅         |       ✅          |
| **Row- and column-level access control**               |              ✅              |            ✅             |         ✅         |         ✅        |
| **Tenant isolation**                                   |              ✅              |            ✅             |    ◐<br>policy     |         ✅         |
| **Multi-dimensional entitlements**                     |    **✅<br>first-class**     |    ◐<br>policy / ABAC     |    ◐<br>policy     | ◐<br>policy / ABAC |
| **Reachability-based entitlements**                    |            **✅**            | ✅<br>explicit FK anchors |         —          |         —          |
| **Aggregate disclosure controls**                      |            **✅**            |            —             |         —           |          —          |
| Logical-plan policy rewriting                          |              ✅              |            ✅            |         ✅          |         ◐          |
| Policy enforcement before optimisation                 |              ✅              |            ✅            |         ✅          |         —          |
| JOIN / CTE / subquery bypass resistance                |              ✅              |            ✅            |         ✅          |         ◐          |
| **Federated query execution**                          |            **✅**            |            —             |         ◐          |         —          |
| **Federated query planning**                           |            **✅**            |            —             |         —          |         —          |
| **Federated query optimisation**                       |            **✅**            |            —             |         —          |         —          |
| **Cross-source joins**                                 |           **✅²**            |            —             |         ◐⁴         |         —          |
| **Virtual tables**                                     |            **✅**            |            —             |         —          |         —          |
| **Indexed in-memory virtual tables**                   |            **✅**            |            —             |         —          |         —          |
| **Partial binding and incremental query optimisation** |            **✅**            |            —             |         —          |         —          |
| **Resource usage limits**                              |           **✅³**            |            ◐             |         ✅          |         ◐         |
| SQL-defined UDFs                                       |            **✅**            |            —             |         —          |         —          |
| Native / application UDFs                              |            **✅**            |            —             |         —          |         —          |
| **Local-only / non-pushdown UDFs**                     |            **✅**            |            —             |         —          |         —          |
| Embeddable as a library                                |            **✅**            |            —             |         ◐          |         —          |
| Primary deployment                                     |        **Embedded**          |     Service / proxy      | Embedded / service |  Service / proxy   |
| Primary runtime                                        |  **.NET + JVM / Calcite**    |    Rust / DataFusion     | Rust / DataFusion  |   Java / Spring    |
| Managed / hosted service                               |             🚫               |            —             |         —          |         —          |
| Admin portal                                           |              —               |            ✅            |   ◐<br>Dashboard   |         ✅         |
| Source-data persistence                                |             🚫               |            —             |    ✅<br>Iceberg   |         —          |

**Legend:** ✅ supported · ◐ achievable through a more general mechanism or only partially comparable · — not demonstrated/documented · 🚫 deliberately not provided by ChalkQL.

**1.** Read-only `SELECT`. No DML (planned), DDL or transactions.

**2.** Cross-source joins depend on data-source capabilities. Complex correlated `LATERAL` joins may currently be refused by the query planner as a precautionary measure due to an Apache Calcite planning limitation.

**3.** An optional working-set limit bounds memory available to query execution.

**4.** SQE supports cross-catalog querying; this is not necessarily equivalent to heterogeneous federated planning across arbitrary source types.

The comparison reflects publicly documented capabilities and is intended to distinguish architectural models rather than imply that an undocumented capability cannot be implemented by another project.

## Indicative numbers

ChalkQL’s vectorised executor is designed to keep managed allocation out of the hot path.

The figures below are indicative measurements for single-threaded toy query execution over 10M rows. DuckDB is invoked in-process via `DuckDB.NET` and explicitly configured with `SET threads = 1` for a like-for-like comparison. Depending on workload, use of multiple threads may significantly reduce wall-clock execution time.

For each query, elapsed time is normalised to DuckDB at `1.00` (lower is faster); DuckDB is included as a familiar native baseline.

| Method                            | Categories | Rows    | Ratio | Gen0      | Allocated     | Alloc Ratio |
|---------------------------------- |----------- |-------- |------:|----------:|--------------:|------------:|
| 'chalkql group by'                | group by   | 2016000 |  1.39 |         - |       2.55 KB |        0.50 |
| 'duckdb group by'                 | group by   | 2016000 |  1.00 |         - |       5.07 KB |        1.00 |
|                                   |            |         |       |           |               |             |
| 'chalkql hop'                     | hop        | 2016000 |  0.77 |         - | 1850175.04 KB |       60.51 |
| 'duckdb hop'                      | hop        | 2016000 |  1.00 | 3000.0000 |    30576.6 KB |        1.00 |
|                                   |            |         |       |           |               |             |
| 'chalkql scan + filter + project' | scan       | 2016000 |  0.87 |  166.6667 |    1636.31 KB |        0.36 |
| 'duckdb scan + filter + project'  | scan       | 2016000 |  1.00 |  400.0000 |     4555.1 KB |        1.00 |
|                                   |            |         |       |           |               |             |
| 'chalkql sort'                    | sort       | 2016000 |  0.28 |         - |    1636.45 KB |        0.20 |
| 'duckdb sort'                     | sort       | 2016000 |  1.00 | 1000.0000 |    8195.37 KB |        1.00 |
|                                   |            |         |       |           |               |             |
| 'chalkql window'                  | window     | 2016000 |  0.62 |         - |    1636.85 KB |        0.20 |
| 'chalkql window (clustered)'      | window     | 2016000 |  0.47 |         - |    1636.88 KB |        0.20 |
| 'duckdb window'                   | window     | 2016000 |  1.00 | 1000.0000 |    8195.37 KB |        1.00 |

GC counts are BenchmarkDotNet collections per 1,000 benchmark operations. They are included because total allocated bytes do not show whether allocation pressure remains short-lived or reaches older generations.

These are not intended as a database shoot-out. DuckDB is deliberately prevented from exploiting multi-threaded execution here, while ChalkQL still has substantial headroom remaining, particularly in aggregation and window execution. The figures should therefore be read as an early comparison of single-thread execution characteristics rather than as a statement about either system’s maximum throughput.

### Predictable execution

ChalkQL deliberately prioritises predictable memory use and low managed allocation alongside execution throughput. Scratch and intermediate memory are served from reusable arenas; an engine owns a bounded `ArenaPool` by default, with excess concurrent executions receiving transient arenas which are released when they finish. Hosts may also supply separate arena pools to partition retained memory between workloads.

## Approaches worth overthinking

Several open-source projects approach data access, authorisation, and querying in ways worth contemplating:

* [**Triceps**](https://triceps.sourceforge.net) — a toolkit for building your own CEP when you might want replayable event streams with time-travel debugging.
* [**Elixir Ecto**](https://github.com/elixir-ecto/ecto) — a sublime non-ORM for composable querying.
* [**Hasura**](https://github.com/hasura/graphql-engine) — summoning the power of declarative data access.
* [**Authz**](https://github.com/eko/authz) — treating authorisation as expressive application policy.
* [**SpiceDB**](https://github.com/authzed/spicedb) — modelling authorisation as reachability through a relationship graph.
* [**CQEngine**](https://github.com/npgall/cqengine) — making querying application-owned collections delightful.
* [**Cayuga**](https://sourceforge.net/projects/cayuga/) — formal query semantics and scalable stream processing needn't be opposing goals.
* [**BabyKusto**](https://github.com/davidnx/baby-kusto-csharp) — the little .NET engine that could, with a pleasant query language for mere humans.
* [**Apache Calcite**](https://github.com/apache/calcite) — the venerable relational parsing, planning, and optimisation foundation on which ChalkQL rests.
* [**Readyset**](https://github.com/readysettech/readyset) — an incremental-dataflow engine for materialising just about anything SQL-shaped. Also witchcraft.

There’s a lot to consider.

## A kindred spirit

[**calcite-dotnet**](https://github.com/ikvmnet/calcite-dotnet) builds upon the remarkable [**IKVM**](https://github.com/ikvmnet/ikvm) to provide in-process interoperability between Apache Calcite and the .NET runtime.
The tenacity required to see such an ambitious integration through is itself a source of motivation, and its evolution may offer ChalkQL a future path towards deeper in-process integration.

## Project status

ChalkQL is under active development. APIs, policy semantics, and planner behaviour may change as the project evolves.

Feedback, experiments, adversarial SQL, and contributions are welcome.

_Technically_ sound, it was tempting to name the library _Derpinator_, in reference to preventing derpy agentic behaviour.

<img width="56" height="56" alt="derp_smiley" src="docs/assets/derp_smiley.svg">