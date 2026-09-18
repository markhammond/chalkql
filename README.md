# **Democratise access to your data with confidence.**

[![NuGet](https://img.shields.io/nuget/v/ChalkQL.svg)](https://www.nuget.org/packages/ChalkQL) [![ci](https://github.com/markhammond/chalkql/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/chalkql/actions/workflows/ci.yml)

ChalkQL applies zero-trust principles to SQL, rewriting queries to enforce need-to-know access, with tenant isolation across federated data sources. *Federated SQL query planning and optimisation for .NET, powered by <a href="https://calcite.apache.org">Apache Calcite</a>.*

Targets .NET 10. JDK 21+ needed for Apache Calcite planner.

## Why ChalkQL

The objective is simple: just write the SQL you mean and let ChalkQL enforce fine-grained authorisation. Applications and agents increasingly need to query data they do not completely own, across sources with different capabilities and trust boundaries. Access control is too often entangled with individual queries, views, ORMs, or application code — conflating query intent with what a principal is permitted to know.

### Live streaming pivots _using_ idiomatic SQL.

The <a href="docs/tutorial.md">tutorial</a> builds from ordinary C# objects and federated queries through to a live inventory view combining streaming state, temporal joins and tenancy-aware access control.

### Where separation of concerns matters.

Applications submit ordinary SQL expressing the data they need. Independent policies govern what the requesting principal is permitted to access or derive. ChalkQL binds the principal and rewrites the relational plan to enforce those policies prior to execution. The rewritten plan is then optimised across heterogeneous data sources.

### Domain-specific entitlements.

Entitlements are application-defined and may incorporate application state, relationships, roles, resource scopes, or other domain-specific policy, and can govern:

* **Rows and columns** — restrict which records and values a principal may access.
* **Tenant isolation** — constrain access to the appropriate tenant or resource scope, including transitive relationships.
* **Aggregate disclosure** — permit approved statistical aggregates over protected values without granting direct access to those values.
* **Multi-dimensional resource scopes** — resolve access through different relationships to the same resource; for example, a franchise owner may access their stores while an auditor accesses stores within their region.

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

Treating SQL as a _mathematically closed language_ is foundational to ChalkQL’s security architecture. ChalkQL embeds access-control policies into query execution. Structural enforcement at the query-plan level prevents access controls from being bypassed through joins, subqueries, CTEs, and aggregation.

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

## Approaches worth overthinking

Several open-source projects approach data access, authorisation, and querying in ways worth contemplating:

* [**Elixir Ecto**](https://github.com/elixir-ecto/ecto) — a sublime non-ORM for composable querying.
* [**Hasura**](https://github.com/hasura/graphql-engine) — summoning the power of declarative data access.
* [**Authz**](https://github.com/eko/authz) — treating authorisation as expressive application policy.
* [**SpiceDB**](https://github.com/authzed/spicedb) — modelling authorisation as reachability through a relationship graph.
* [**CQEngine**](https://github.com/npgall/cqengine) — making querying application-owned collections delightful.
* [**Cayuga**](https://sourceforge.net/projects/cayuga/) — formal query semantics and scalable stream processing needn't be opposing goals.
* [**BabyKusto**](https://github.com/davidnx/baby-kusto-csharp) — the little .NET engine that could, with a pleasant query language for mere humans.
* [**Apache Calcite**](https://github.com/apache/calcite) — the venerable relational parsing, planning, and optimisation foundation on which ChalkQL rests.

## A kindred spirit

[**calcite-dotnet**](https://github.com/ikvmnet/calcite-dotnet) builds upon the remarkable [**IKVM**](https://github.com/ikvmnet/ikvm) to provide in-process interoperability between Apache Calcite and the .NET runtime.
The tenacity required to see such an ambitious integration through is itself a source of motivation, and its evolution may offer Chalk a future path towards deeper in-process integration.

## Indicative numbers

ChalkQL’s vectorised executor takes care to keep managed allocation out of the hot path. Scratch and intermediate memory are served from reusable arenas; an engine owns a bounded arena pool by default, and hosts may provide separate pools to partition workloads.

The figures below are indicative toy-query measurements over 10M rows. Runtime is shown relative to ChalkQL rather than as absolute wall-clock time; DuckDB is included as a familiar native baseline.

| Query                   | Engine  | Runtime |   Gen0 |  Gen1 |  Gen2 | Allocated |
| ----------------------- | ------- | ------: | -----: | ----: | ----: | --------: |
| scan + filter + project | ChalkQL |    100% |  166.7 |     — |     — |   1.60 MB |
|                         | DuckDB  |     65% |  600.0 | 100.0 | 100.0 |   4.56 MB |
| group by                | ChalkQL |    100% |      — |     — |     — |   2.43 KB |
|                         | DuckDB  |      7% |      — |     — |     — |  11.46 KB |
| sort                    | ChalkQL |    100% |      — |     — |     — |   1.60 MB |
|                         | DuckDB  |     92% | 1000.0 |     — |     — |   8.00 MB |
| window                  | ChalkQL |    100% |      — |     — |     — |   1.60 MB |
|                         | DuckDB  |     31% | 1000.0 |     — |     — |   8.00 MB |
| hopping window          | ChalkQL |    100% |      — |     — |     — |   6.03 MB |
|                         | DuckDB  |     90% | 3000.0 |     — |     — |  29.86 MB |

GC counts are BenchmarkDotNet collections per benchmark operation. They are included because total allocated bytes do not show whether execution pressure remains short-lived or reaches older generations.

These are not intended as a database shoot-out. ChalkQL’s execution engine is currently single-threaded and deliberately prioritises embedding, federation, predictable memory use and low managed allocation alongside operator throughput.

There is substantial headroom remaining — particularly in aggregation and window execution — and the current implementation should be read as an early performance snapshot rather than a throughput ceiling.

`ArenaPool` keeps a bounded number of `ExecutionArena`s warm between executions; excess concurrent executions receive transient arenas which are released when they finish. Hosts may also supply their own arena pools to isolate retained memory between workloads.

## Project status

ChalkQL is under active development. APIs, policy semantics, and planner behaviour may change as the project evolves.

Feedback, experiments, adversarial SQL, and contributions are welcome.