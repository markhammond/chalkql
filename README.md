**ChalkQL** applies zero-trust principles to SQL, rewriting queries to enforce need-to-know access, with tenant isolation across federated data sources. *Federated query planning and optimisation powered by <a href="https://calcite.apache.org">Apache Calcite</a>.*

**Democratise access to your data with confidence.**

## Why ChalkQL?

The objective is simple: **just write the SQL you mean; let ChalkQL enforce fine-grained authorisation.** Applications and agents increasingly need to query data they do not completely own, across sources with different capabilities and trust boundaries. Access control is too often entangled with individual queries, views, ORMs, or application code — mixing what an application wants to ask with what a principal is permitted to know.

**Where separation of concerns matters.**

Applications submit ordinary SQL expressing the data they need. Entitlements independently express what the requesting principal is permitted to access or derive. ChalkQL binds the principal and rewrites the relational plan to enforce the applicable entitlements before execution. The resulting query is then planned and optimised across heterogeneous data sources.

**Application-defined access policies.**

Entitlements may incorporate application state, relationships, roles, resource scopes, or other domain-specific policy, and can govern:

* **Rows and columns** — restrict which records and values a principal may access.
* **Tenant isolation** — constrain access to the appropriate tenant or resource scope, including transitive relationships.
* **Aggregate disclosure** — permit approved statistical aggregates over protected values without granting direct access to those values.
* **Multi-dimensional resource scopes** — resolve access through different relationships to the same resource; for example, a franchise owner may access their stores while an auditor accesses stores within their region.

**Federation for (almost) everyone.**

ChalkQL brings Apache Calcite's federated relational query planning and optimisation to .NET applications. Operations are pushed down when supported by the underlying source, with ChalkQL's embedded vectorised execution engine handling the remainder locally. This allows a single relational plan to span heterogeneous remote sources and application-provided virtual tables.

**Bring your own data.**

ChalkQL introduces no persistence layer: data is queried on demand, wherever it resides.

* Cross-source joins, subject to data-source capabilities.
* Partial binding and incremental query optimisation.
* Virtual tables backed by application data.
* Indexed in-memory virtual tables.
* Register SQL-defined, host-language, and native UDFs with pushdown.
* Host-controlled resource usage during query execution.

## Access by construction

ChalkQL treats authorisation as part of query execution rather than a convention that every query author must remember.

Policies are applied to the relational plan before execution, allowing enforcement to remain effective as SQL is composed through joins, subqueries, CTEs, and aggregation.

Entitlement enforcement is opt-in, with no policy overhead for queries that do not invoke it.

## How does ChalkQL compare?

The closest open-source projects overlap with different parts of ChalkQL. BetweenRows focuses on policy-enforced SQL proxying, SQE combines DataFusion query execution with fine-grained access control, and AccessFlow provides broad data-access governance and proxying.

| Capability                                             |        **ChalkQL**       |      BetweenRows      |         SQE        |    AccessFlow   |
| ------------------------------------------------------ | :----------------------: | :-------------------: | :----------------: | :-------------: |
| Arbitrary SQL                                          |             ✅            |           ✅           |          ✅         |        ✅        |
| **Row- and column-level access control**               |             ✅            |           ✅           |          ✅         |        ✅        |
| **Tenant isolation**                                   |             ✅            |           ✅           |          —         |        ✅        |
| **Multi-dimensional resource scoping**                 |     **✅ <br>first-class**    |    ◐ <br>policy / ABAC    |          —         | ◐ <br>policy / ABAC |
| **Transitive resource / tenancy scoping**              |           **✅**          | ✅ <br>explicit FK anchors |          —         |        —        |
| **Aggregate disclosure gating**                        |           **✅**          |           —           |          —         |        —        |
| Logical-plan policy rewriting                          |             ✅            |           ✅           |          ✅         |        ◐        |
| Policy enforcement before optimisation                 |             ✅            |           ✅           |          ✅         |        —        |
| JOIN / CTE / subquery bypass resistance                |             ✅            |           ✅           |          ✅         |        ◐        |
| **Federated query execution**                          |           **✅**          |           —           |          ◐         |        —        |
| **Federated query planning**                           |           **✅**          |           —           |          —         |        —        |
| **Federated query optimisation**                       |           **✅**          |           —           |          —         |        —        |
| **Cross-source joins**                                 |          **✅¹**          |           —           |         ✅²         |        —        |
| **Virtual tables**                                     |           **✅**          |           —           |          —         |        —        |
| **Indexed in-memory virtual tables**                   |           **✅**          |           —           |          —         |        —        |
| **Partial binding and incremental query optimisation** |           **✅**          |           —           |          —         |        —        |
| **Resource usage limits**                              |          **✅³**          |           ◐           |          ✅         |        ◐        |
| SQL-defined UDFs                                       |           **✅**          |           —           |          —         |        —        |
| Native / application UDFs                              |           **✅**          |           —           |          —         |        —        |
| **Local-only / non-pushdown UDFs**                     |           **✅**          |           —           |          —         |        —        |
| Embeddable as a library                                |           **✅**          |           —           |          ✅         |        —        |
| Primary deployment                                     |       **Embedded**       |    Service / proxy    | Embedded / service | Service / proxy |
| Primary runtime                                        | **.NET + JVM / Calcite** |   Rust / DataFusion   |  Rust / DataFusion |  Java / Spring  |
| Managed / hosted service                               |             ❌            |           —           |          —         |        —        |
| Admin portal                                           |             ❌            |           ✅           |          —         |        ✅        |
| Source-data persistence                                |             ❌            |           —           |          ◐         |        —        |

**Legend:** ✅ supported · ◐ achievable through a more general mechanism or only partially comparable · — not demonstrated/documented · ❌ deliberately not provided by ChalkQL.

**1.** Cross-source joins depend on data-source capabilities. Complex correlated `LATERAL` joins may currently be refused by the query planner as a precautionary measure due to an Apache Calcite planning limitation.

**2.** SQE supports cross-catalog querying; this is not necessarily equivalent to heterogeneous federated planning across arbitrary source types.

**3.** ChalkQL currently exposes a **host query execution working-set limit**, allowing the embedding application to bound the working memory available to query execution.

The comparison reflects publicly documented capabilities and is intended to distinguish architectural models rather than imply that an undocumented capability cannot be implemented by another project.

## Architecture

ChalkQL is predominantly an embedded **.NET library**. Apache Calcite query planning and optimisation are provided by a lightweight JVM sidecar. ChalkQL's vectorised execution engine runs within the .NET host, executing relational operations that cannot or should not be pushed down to an underlying source.

```text id="s4z9tq"
                         ┌─────────────────────┐
                         │   .NET application  │
                         └──────────┬──────────┘
                                    │
                         ┌──────────▼──────────┐
                         │       ChalkQL       │
                         │                     │
                         │ policy · binding    │
                         │ vectorised execution│
                         └──────────┬──────────┘
                                    │
                         ┌──────────▼──────────┐
                         │   Apache Calcite    │
                         │    JVM sidecar      │
                         │                     │
                         │ rewrite · plan      │
                         │ optimise · federate │
                         └──────────┬──────────┘
                                    │
                ┌───────────────────┼───────────────────┐
                │                   │                   │
        ┌───────▼───────┐   ┌───────▼───────┐   ┌──────▼──────┐
        │ Remote source │   │ Remote source │   │Virtual table│
        └───────────────┘   └───────────────┘   └─────────────┘
```

The host retains control of application lifecycle and execution policy, while Calcite provides the relational planning and optimisation substrate.

## Design influences

ChalkQL owes ideas to several projects that approach data access, authorisation, and querying from very different directions:

* **Elixir Ecto** — a sublime non-ORM for composable querying.
* **Hasura** — summoning the power of declarative data access.
* **Authz** — treating authorisation as expressive application policy.
* **CQEngine** — making querying application-owned collections delightful.
* **Apache Calcite** — the venerable relational planning and optimisation foundation on which ChalkQL rests.

ChalkQL aims to contribute something complementary: making accessibility and governance a natural part of query execution, while helping address the dearth of embeddable query-planning and optimisation infrastructure for .NET applications.

## Status

ChalkQL is under active development. APIs, policy semantics, and planner behaviour may change as the project evolves.

Feedback, experiments, adversarial SQL, and contributions are most welcome.
