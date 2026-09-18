# `edge` — the coverage graft's adversarial family

**Provenance.** The queries in this directory are grafted from
[`ikvmnet/calcite-dotnet`](https://github.com/ikvmnet/calcite-dotnet) (Apache-2.0, Copyright 2025
Jerome Haltom and others), principally `src/Apache.Calcite.Tests/ClrEnumerableDifferentialTests.cs`
and `ClrEnumerableRelTests.cs`. See the repository's `NOTICE`. **Only the queries and the fixture
shapes are taken; no expected value is** (D163): that project treats Calcite's enumerable engine as
its specification and pins Calcite's defects on purpose, where Chalk's specification is SQL. Every
answer here is derived from the reference executor and from DuckDB exactly as every other corpus
query's is, and `docs/adr/0024-coverage-graft.md` names the cases where Chalk's standard-conforming
answer differs from the one that suite asserts.

**Why it is not a milestone.** This family cuts across sort and fetch (M1), joins (M3), windows
(M4/M5), set operations (M6) and `UNNEST` (M5). Splitting it into four would scatter one fixture
across four families and force four sets of plans to be re-recorded; a single cross-cutting family
with a non-milestone name is the honest shape (D165).

**The fixtures.** Two hand-written literal tables (D164, `docs/design/25-coverage-graft.md` §1) — an
explicit exception to the generated-fixture rule of `05-testing.md` §2, because their value is in the
exact ties and NULLs they hold:

| Table | Rows | What it is for |
|---|---|---|
| `sales(id, region, amount NULL, label)` | `(1,EAST,10,A) (2,EAST,20,B) (3,EAST,20,C) (4,WEST,30,D) (5,WEST,NULL,E) (6,WEST,5,F)` | two partitions, a duplicate `amount`, one NULL, **no** declared collation and **no** unique key |
| `sorted(k, v)` | `(1,A) (2,B) (2,C) (4,D)` | declared ordered by `k` with **no** unique key, so `k` repeats at 2 and skips 3 |

Both live in `Chalk.TestKit.Fixtures` and are loaded into the POCO, SQLite, DuckDB and PostgreSQL
fixtures, so the family runs at every pushdown level and against the DuckDB oracle.

## Groups

| Prefix | Group | Count | What it exercises |
|---|---|---|---|
| `a1_` | Limit and sort with NULLs and ties | 25 | `NULLS FIRST`/`LAST` both directions, a tie spanning a fetch boundary, an offset landing inside a tie, an offset past the end, `FETCH 0`, two keys where the first ties, `DISTINCT` then fetch |
| `a2_` | Nullable and null-safe join keys | 22 | inner, left, right, full, semi and anti over a nullable key; a composite key with one nullable member; `IS NOT DISTINCT FROM`; an extra non-equi predicate per join type; a join over `sorted`'s duplicate key and its gap |
| `a3_` | Degenerate window frames | 20 | a frame entirely in the past and one entirely following, an always-empty frame, a window over zero rows and over a filtered input, `PARTITION BY` the nullable key, `ROW_NUMBER()` with no `ORDER BY`, two `COUNT`s in one window, `LEAD`/`LAG` with offset and default, `NTILE`, `NTH_VALUE`, `EXCLUDE CURRENT ROW`/`TIES`/`GROUP` |
| `a4_` | Set operations over nullable keys | 16 | union ALL and DISTINCT ordered by a nullable key under both null placements, with a fetch, with offset and fetch, three inputs; `INTERSECT ALL` and `EXCEPT ALL` |
| `a5_` | `UNNEST` additions | 5 | a NULL array, an empty array, a NULL element, `UNNEST` beside another input and `WITH ORDINALITY` beside another input. **Not** an array of one-field or two-field rows: `TypeKind` has no ROW and a LIST element is never a LIST, so v1 lists are one level deep of a scalar (V57, D58). And the NULL and empty arrays come from a column, because Calcite can plan neither as a literal (V48, V49) |
| `g_` | Projection-position operators, and the character set | 2 | `IS DISTINCT FROM` in a projection; a non-Latin-1 literal and parameter (§0b) |

## Headers

Beyond the usual `-- expect:` lines, a query here may carry `-- compare: top-k-under-ties` (D166):
its `LIMIT` boundary falls inside a tie, so the answer is determined everywhere except at the
boundary keys, where only the count is. Everything without that header keeps the rule of
`10-conformance-and-fixes.md` §1.
