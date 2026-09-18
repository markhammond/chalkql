# `m7-adversarial` — statements written to subvert the entitlement rewrite

Forty statements, in the conventions of `m7-tenancy` beside it, whose purpose is to **fail**:
each one is an attempt to make the leaf rewrite of `docs/design/16-entitlements.md` §3 disclose
something the policy of §8 says the principal may not have. The design is
`docs/design/32-adversarial-entitlements.md` (D251–D253); the threat model is its §0, and the short
of it is that the attacker controls **the statement text and its parameters and nothing else** —
the host binds the principal, registers the catalog and chooses the options.

Every file's first lines name the rule of §3 the statement targets and say why it must fail. The
headers are `m7-tenancy`'s: `-- expect: principals(all)`, `-- expect: policy(<principal>)` for a
principal the statement is a `POLICY` refusal for, and one header this family adds —
`-- expect: known-limit`, for the one statement that records a limit rather than a defence (below).

## What runs them

- **In process, against the oracle and a golden** — `TenancyCorpusTests`, theory
  `Every_subversion_is_held_to_the_oracle_and_the_golden`, as every one of the nine principals
  `TenancyFixture` names. The rows must be the rows `TenancyOracle` — §8's model stated naively in
  C#, with no descriptor, no planner and no pass — discloses to that principal, and they must be
  what `corpus/plans/m7-adversarial/` records.
- **Over a database, at every pushdown level** — `TenancyRemoteBindingTests`, theories
  `Every_subversion_over_a_database_learns_what_it_learned_in_process` and
  `Every_subversion_learns_the_same_with_the_tenancy_folded`. What a statement learns must not
  depend on how much of it the source evaluated, so the level is part of the case.
- **D252's detector, on every run of all three.** Every value the policy can mask or hide carries a
  unique token; a principal's allowed tokens are the ones in the values the oracle discloses to
  them, and every other token is forbidden in the rows, the plan text, the disclosure report, any
  refusal or exception message, the SQL sent to a source, and the statistics record.

A statement never quotes a canary. The attacker does not know the stored value — that is what makes
a guess a guess — and a statement that quoted one would put a forbidden token in its own plan text,
where the detector would report the attacker's own guess back as a leak.

What is not a statement is `Chalk.Integration.Tests.EntitlementSubversionTests`: narrowing a plan
towards a tenant it was not bound to, the pushdown levels and capability sets a host may set, the
plan text and refusal text of every statement of both families, the client body that says what it
was handed, and what the tree does with a statement that spells `@ctx` itself.

## The classes, and the count

| # | Class | Statements | The rules it targets |
|---|---|---|---|
| 1 | Probes on a masked or hidden column | 01–19 (19) | §3.1 a predicate compares the disclosed value; §3.11 stars and placeholders; §3.4 population-only |
| 2 | Joins as oracles | 20–28 (9) | §3.8 masks stay local; §3.7 the row predicate reaches the source; §3.13 through a parent; D227 |
| 3 | Set operations | 29–33 (5) | §3.1, §3.12 the reported disclosure of a union |
| 4 | Indirection | 34–39 (6) | §3.1 the rewrite is at the leaf; §7 SQL and client bodies |
| — | D253's known limit | 40 (1) | not defended, and recorded as such |

## D253 — the limit this family records rather than defends

`40_known_limit_differencing_on_the_guard` is run and its golden recorded, and it is **never**
compared with the oracle. The group-size guard NULLs an aggregate over a group below the floor; two
aggregates over sets that differ by exactly that group, each above the floor, give it back by
subtraction. That is query-set-size control's documented limit rather than a defect in the rewrite —
defending it is differential privacy's problem — and the statement is here so a reader meets the
limit in the corpus instead of having to know it. Timing and cardinality side channels are the other
half of D253: not defended, and not measured.

## What the battery found

A successful attack is the point of the family, not a failure of it. A statement whose rows differ
from the oracle's, or which fails where the policy requires no refusal, is registered as an F-number
in `docs/design/07-decisions.md` and named in the `KnownLeaks` set the theories carry; the statement
stays exactly as written and the fix is its own run. Eight defects are registered, F58–F65, and only
one of them — F58 — is a principal receiving an answer the oracle says they may not have; the other
six fail **closed**, with a refusal, an unsupported feature or a plan the client's own invariant
check rejects. **F58 is fixed** (ADR 0038), in its own run as the rule says: statements 08, 17 and 18
are out of `KnownLeaks` and held to the oracle as every principal like any other. **F61 and F65 are
closed** by the F66 fix (ADR 0050) and their statements, 23 and 49, are out of `NotPlanned` from
2026-09-16: both are run as every principal, both agree with the oracle, the detector reports
nothing of either, and both carry a golden. The statements themselves are untouched, this note
included.

| Statement | Registered | What it found |
|---|---|---|
| 08, 17, 18 | F58 — **fixed**, ADR 0038 | An expression over a redacted column is resolved against the base column's declared nullability rather than the stand-in's: `IS NOT NULL` is true for every visible row where the oracle says none, `COALESCE(c, 'x')` answers NULL, and `CHAR_LENGTH(c)` answers 0. An entitled table now publishes to the validator and the converter a row type in which a column any rule can withhold is nullable, so the statement's own expressions are simplified under the stand-in's nullability |
| 14 | F59 | `STRING_AGG`/`LISTAGG` over an entitled table whose row predicate folded to FALSE fails with "a non-constant separator" |
| 16 | F60 | A window whose order key folds to a constant stand-in fails inside Calcite with "Expected identity mapping" |
| 23 | F61 | A correlated `IN` over two occurrences of one entitled table did not finish planning for `u3`. **Closed by the F66 fix** (ADR 0050): the same pair of Hep rules undoing each other, `JOIN_PUSH_TRANSITIVE_PREDICATES` and `PruneEmptyFilter`. Out of `NotPlanned` since 2026-09-16, held to the oracle and the detector as every principal, its golden recorded |
| 24, 25 | F62 | A sub-query over a table with a population-only column is refused even where the sub-query never reads that column |
| 27 | F63 | Pinning a `through` child's correlation key to a literal trips the taint check's own parent-join clause |
| 49 | F65 | An **uncorrelated** `IN (SELECT …)` over the entitled table did not finish planning either. F61's twin, found by D261's own additions and not caused by them (ADR 0042). The principal is **`u2`**, not the `u1` the run first blamed: `u1` plans in 820 ms on a fresh sidecar and `u2` is what wedges it, after which every later principal times out. **Closed by the F66 fix**, out of `NotPlanned` since 2026-09-16, its golden recorded |
| 31 | F64 | `INTERSECT` between an entitled column and a literal list builds a plan the client's nullability invariant rejects. Since the F58 run it is every principal that reads a row of the entitled branch rather than the three whose folded rules happened to produce a typed NULL: the disclosed row type is the same for all of them (D161) |

**The tested column (D261, §36).** Statements 41–49 are class 6: every shape the support desk's rule
does *not* name, over the column it may test. `LIKE`, a function of the column, a comparison with a
column, `IS NULL`, a sort key, a grouping key, a `VALUES` list joined on it, `IN (SELECT …)`, and a
`CASE` whose condition tests and whose branch is the value. Each of them reads the **placeholder**,
which is what a shape the policy did not name is worth; 44 is the one worth reading twice, because
the condition *is* a permitted shape and the branch is not — the leaf computes the one and hands back
the stand-in for the other. What the rule does permit is the tenancy family's 29–34.

**Conjoined confinement (D266, §40).** Statements 50–52 are class 7: the three ways a principal
whose one grant is confined along two tenancy kinds at once might try to recombine the halves. 50
asks for the same organisation *outside* the region — a confined grant is not an organisation grant
with a filter on top, and both columns of its tuple must match at once. 51 unions a region-only
probe with an organisation-only probe, neither of which is reachable alone for such a principal. 52
joins through `members` back to `orders` to reach an order outside the confinement by way of the
member who placed one inside it; `members` resolves no region, so a confined grant reaches no member
row and the correlation has nothing to stand on. All three answer with exactly the rows the
principal was already entitled to, and the detector reads every word of each answer.

Three statements are not run in one place or another, each for a reason that is not about this
family's subject and each named in the theory that skips it. `49` and `23` are no longer among
them: both plan since the F66 fix and both are held to the oracle and the detector in process like
any other statement (ADR 0050, and the un-exclusion of 2026-09-16), and `23`'s remote folded run —
which returned the whole outer table to the global grant where the statement's own membership
admits three rows — is fixed as **F72** (ADR 0059), so it runs over a database at every pushdown
level too. `14` and `40`
are not run over a database (F59 at a second locus, and F57's `SUM` over a `BIGINT`); `26` names
`notes`, which the database fixture does not hold; and `37` is a `CROSS JOIN`, which has no equality
for a lookup join and so puts a remote query on a nested loop's inner side — the statement's own
shape rather than the policy's, and it does its work in process.
