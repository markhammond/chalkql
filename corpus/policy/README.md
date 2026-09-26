# `corpus/policy` — the entitlement conformance battery

The statements, one file per case. Everything else the battery is — the fixture's rows, the tables'
entitlements in layer A's vocabulary, the principals as bound contexts, and what every case must
produce — is **code**, in `dotnet/tests/Chalk.TestKit`:

| Where | What it holds |
|---|---|
| `cases/<nnn>-<slug>.sql` | 229 statements, one file per case — the one part of a case worth reading on its own |
| `PolicyFixture.Rows.cs` | the tenancy fixture: 3 orgs, 10 members, 16 orders, 6 notes, 4 invites, 4 symbols, and D265 clause (h)'s 2 vendors, 3 items and 5 order lines, every row a literal |
| `PolicyColumns.cs` | where each column sits in its table, which is what an entitlement's ordinal means |
| `PolicyEntitlements.cs` | the tables' entitlements in **layer A's vocabulary** — a row predicate, ordered `DisclosureRule`s, masks, placeholders, an aggregate allow-list and a floor — plus every variant a case switches to, and the four descriptors that must be refused at registration |
| `PolicyPrincipals.cs` | 22 principals as bound `RequestContext`s |
| `PolicyAdoFixture.cs` | the same six tables and rows in DuckDB and PostgreSQL, under the four declared source profiles, for the fifteen cases that name one. Group U's three are the in-process fixture's alone: no case that names a source profile reads a vendor, an item or a line |
| `PolicyCases.Corpus.cs`, `PolicyCases.Calcite.cs` | one `PolicyBatteryCase` per case: principal, binding, catalog variant, source, options, the expected rows or refusal, the expected labels, the expected report, the claims, a `Notes` line naming the design section it exercises, and — where the engine and the case disagree — the `PolicyAdjudication` that judges it |
| `PolicyCases.cs` | the two lists joined, and README §5's uncertainties with what the run answered |
| `Chalk.Integration.Tests/PolicyBatteryTests.cs` | the run: one `[Theory]` row per case, plus the tally and the uncertainties |

It was written as YAML and converted in step 26 part 2e, because test configuration belongs in code:
a catalog variant, a principal, an option, a column and a disclosure are all names something else
declares, and a rename that misses one should stop the build rather than leave a case quietly
testing nothing. The semantics encoded are those of
[`docs/design/16-entitlements.md`](../../docs/design/16-entitlements.md) and
[`docs/adr/0025-entitlements.md`](../../docs/adr/0025-entitlements.md); where the design is silent
the case's `Notes` line says so and §5 below lists every one of them.

**225 cases** — 200 numbered and 25 Calcite guards. Case numbers are three digits, not the two the task's shape suggests: 120+ cases do
not fit in two. The Calcite guards are numbered `c01` upward.

## How to add a case

1. Write the statement in `cases/<nnn>-<slug>.sql`.
2. Add a `PolicyBatteryCase` to `PolicyCases.Corpus` — or to `PolicyCases.Calcite` if it guards a
   named Calcite defect — naming that file. State only what differs from `PolicyCatalog.Default` and
   `PolicyCaseOptions.Default`.
3. Compute the expected rows **by hand** from the fixture under that principal's context. That is
   the whole point: a golden can only tell you the engine changed, and this can tell you it is
   wrong.
4. Run it. If the engine disagrees, judge the disagreement and put the judgement on the case as a
   `PolicyAdjudication` — the engine's answer, the case's answer, and which is right. Never edit the
   expectation to match the engine.
5. `The_battery_is_the_size_the_run_reported` counts what is there, so a case added or lost shows up
   as a number.

## 1. The coverage matrix

| Group | Cases | n | Design sections and decisions |
|---|---|---|---|
| **A** the design's own corpus | 001–042 | 42 | §8 corpus 01–15b, 20, 24, 25, run as every principal it makes sense for; §3.1 D195, §3.2 D196, §3.3, §3.4 D198, §3.6, §3.11 D160–D162, §3.12 D202/D207, D204, D206 |
| **B** every disclosure kind in every position | 043–056 | 14 | §3.1, §3.5, §3.12 — Full, Masked, Undisclosed, PerRow and Aggregate in a projection, a filter, a join key, a grouping key, a sort key, a window key and an aggregate argument |
| **C** masked values in predicates and joins | 057–064 | 8 | §3.1 D195, the leaf-sanitisation replacement of the query-field rule; §3.2 (a constant mask hides NULL-ness) |
| **D** the population-only trace | 065–079 | 15 | §3.4 D198, §3.5, D190 — the bare argument, the numeric CAST, and every refused use: expression, filter, join condition, grouping key, sort key, `FILTER`, window, a non-listed aggregate, an aggregate outside D190, and the mixed principal with its remedy |
| **E** the group-size guard | 080–094 | 15 | §3.6, §6 — every allow-listed aggregate guarded including `COUNT(c)` and `COUNT(DISTINCT c)`, `COUNT(*)` unguarded, the boundary at k and one below, `HAVING` and `ORDER BY` over a guarded aggregate, the vacuous floor and k = 0 |
| **F** stars, redacted columns, placeholders | 095–108, 187 | 15 | §3.11 D159–D162, D160's three `StarPolicy` settings, `Placeholder`/`Omit`/`Refuse`, `PlaceholdersAsNull`/`PlaceholdersAsEmpty`, a per-column placeholder, a named redacted column under each setting, and D217's second knob over one (187) |
| **G** statement shapes | 109–125 | 17 | §3 — `EXISTS`, `NOT EXISTS`, `IN` sub-query, scalar sub-queries, `LATERAL`, `UNION`/`UNION ALL`/`INTERSECT`/`EXCEPT`, a CTE, one table referenced twice with different uses, a self-join, and an entitled table joined to an unentitled one |
| **H** folding | 126–132 | 7 | §2 items 1–3, D197 — an empty list, `NOT IN` over an empty list, a composite list, a single-column list, a list above `fold_max_rows`, a scalar wildcard |
| **I** visibility, contradiction, refusal | 133–140 | 8 | §3.12 D207 — `visibility` NONE/SOME/ALL, `contradiction`, `RefuseWhenNoVisibleRows`, one report row per entitled table, none for an unentitled one |
| **J** created-by, subject grants, the global grant | 141–148 | 8 | D204 (`within` and `Anywhere`), D205 (`AllowGlobalGrants`), D206 (`CreatorSees` Full and ByRules, on two tables and across a tenancy boundary) |
| **K** pushdown, locality, the ADO variants | 149–159, 190 | 12 | §3.7 D199 (`row_predicate_pushed`, `PUSHDOWN_REQUIRED` over a profile with and without `IN`), §3.8 D200 (a mask stays local, a full column pushes), D156 (`LOCAL`, `trust_source_row_level_security`), the filter-residual rule of §3.7 item 3, the key-set path, and a composite list above the ceiling shipped as a key set (190; §2 item 2, F50) |
| **L** `statistical` and execute-time binding | 160–170, 186 | 12 | D203 (a k-anonymous group key, a raw predicate under `MASKED` and under `AGGREGATE_ONLY`, group suppression, pinning refused, row-level output refused, a window refused, and the same statement without the opt-in), §2/D209 (execute-time binding, and `Omit` degrading) |
| **M** disclosure sibling columns | 171–176 | 6 | §3.12 D207 — the sibling, the per-row meet on a derived column, no sibling for an unentitled origin, a suffix collision, a configured suffix, an undisclosed sibling |
| **N** the fingerprint join | 177–179 | 3 | §8 corpus 19, §4 — an equality join on tokens within one tenancy, across two, and the contrast with a principal whose two orgs mask differently |
| **O** registration negatives | 180–183 | 4 | §8 corpus 23, §1 — `otherwise: FULL` on a tenanted table (D208), `MAX` in an allow-list (D190), a non-boolean `when`, a rule mask of the wrong type |
| **P** encoding variants | 184, 185, 188, 189 | 4 | D162 (a per-column placeholder on a STRING column), the path dimension spelled as a context list (§2, §5; README §5 A1), and D224's rule placeholder — a rule's own stand-in on a STRING and on a DATE column, and a rule placeholder that is never a query use (§3.11) |
| **Q** conjoined confinement (D266) | 197–200 | 4 | `docs/design/40-conjoined-confinement.md` §1, §4 — a grant confined along two tenancy kinds at once, written in layer A as one membership over a tuple of arity three: the rows all three columns admit, the same tuple with one column changed, two conjoined grants OR-ed, and a column rule whose condition is that same term |
| **T** the test verdict (D261) | 191–196 | 6 | `docs/design/36-test-verdict.md` §2, §3; ADR 0042 — the select-list form of a `TEST` rule, the predicate form, `IN (list)` as one lifted comparison, a shape the rule does not name (`LIKE`), the value itself never disclosed, and the aggregate form under the group-size guard. *These six carried `Group = "M"` in the code until 2026-09-16, a letter this table had already given to the sibling columns; the code now says **T** and the run report agrees with this table.*
| **U** related visibility (D265) | 201–204 | 4 | `docs/design/38-existential-visibility.md` §0, §4, §8 — a row visible through the rows that belong to it, written in layer A as an `Inherited` entry with its steps and its endpoint predicate: an order reached along a `Related` path by a vendor grant alone, the same order where both perspectives meet and the rule order decides, the bridge entitled **by kind** so that a vendor sees the lines carrying its own goods and no other line of an order it can see, and an endpoint predicate that folds to FALSE so the chain is not built at all |
| **CAL** the Calcite guards | c01–c25 | 25 | `PolicyCases.Calcite.cs`, one case per named Calcite defect the pass must survive, grouped in code as `CAL-<x>`: projection permutations and renames (`CAL-P`: c01–c03, c23), ordinals in `ORDER BY` and `GROUP BY` (`CAL-O`: c04, c05, c21), join shapes (`CAL-J`: c06–c08, c24, c25), aggregates (`CAL-A`: c10, c11), set operations (`CAL-S`: c12, c13), correlated sub-queries (`CAL-C`: c14–c16), windows (`CAL-W`: c09, c20), the remote path (`CAL-R`: c17–c19), and the star under `Omit` (`CAL-U`: c22) |

The `n` column sums to 229, which is what `PolicyBatteryTests.The_battery_is_the_size_the_run_reported` asserts: 204 numbered cases and the 25 guards. The letters are this table's; where a run wrote a letter on its cases that the table had already used, the table says so on the row rather than moving a family.

Design §8's corpus, item by item: 01 → 001–009, 041, 042; 02 → 010–013; 03 → 014, 015, 017;
04 → 018, 019; 05 → 020, 021; 06 → 022; 07 → 023, 024, 016; 08 → 025, 026; 09 → 027, 028;
10 → 029, 030 (and 031, which is where the refusal really fires); 11 → 032; 12 → 065–079;
13 → 033, 012; 14 → 004; 15 → 034; 15b → 035, 036; 16 → 149–151, 158, 190; 17 → 095–101;
18 → 102–105; 19 → 177–179; 20 → 037, 136, 137; 21 → 155, 156; 22 → 160–164, 186; 23 → 180–183;
24 → 038; 25 → 039, 040.

## 2. What is not covered here

The negatives of §8 that are not statements — executing without a context, a grant with an unknown
role, a NULL list member at bind, an entitlement referencing a missing column or a broken
association path, a foreign-key path cycle — belong to unit tests rather than to a corpus, and the
three deliberate breakages of the taint check (§3.10) belong to the planner's own suite. The
zero-cost class of §0 is measured against the existing corpus families, not against this one; case
140 is the single query here that touches no entitlement and can therefore be compared
byte-for-byte with its entitlement-free plan. Nothing here asserts an allocation or a wall-clock
number.

**Visibility derived through a parent (§3.13) is not here either**, and the reason is the fixture
rather than the subject. A `through` parent must declare a unique key on the column its child
correlates against, and this fixture's tables are *discovered from the database*, which declares
none — so the family lives where a fixture can state one. The rows, the per-column labels and the
report, per principal and against the independent oracle, are corpus queries 26–28 of
`corpus/queries/m7-tenancy` (a manager, an agent, a participant by subject grant, a global grant
whose join is elided, a principal with no grant, and a two-level chain); the shapes a golden cannot
record as rows — two parents OR-ed, one parent through two columns, a nullable correlation key and
execute-time binding — are `Chalk.Integration.Tests.ThroughTests`; the registration refusals are
`chalk.planner.ThroughRegistrationTest` and `Chalk.Catalog.Tests.ParentVisibilityTests`; the taint
breakage is `chalk.planner.TaintCheckTest`; and the remote half, on DuckDB and on PostgreSQL, is
`Chalk.Integration.Tests.EntitlementsAdoTests`.

## 3. How the battery runs

1. **Load the fixture.** `PolicyFixture.Create(catalog)` registers the six tables as POCO
   collections under the schema the statements name unqualified. `is_vip` is a client-bodied scalar
   function returning true for member ids 1, 5 and 8 (case 158 is the only case that calls it). The
   fixture shares nothing with the run-time fixtures of `docs/design/05-testing.md` §2, and
   `symbols` is not the instrument dimension of the M1 fixture. The ADO runs create the same tables
   in DuckDB and in PostgreSQL and insert the same rows (`PolicyAdoFixture`).
2. **Build the catalog per case.** A case names the entitlement variant per table in its
   `PolicyCatalog`; anything it does not name takes `PolicyCatalog.Default` (`members`, `orders`,
   `notes`, `invites`, and `symbols` with no entitlement at all). The four descriptors named
   `Bad*` are registered only by the four group-O cases, which expect registration itself to fail.
   One engine is built per distinct catalog and shared by every case that names it.
3. **Bind the context.** A case names one principal. Every name any descriptor mentions is bound for
   every principal, empty where the principal holds no grant of that kind. Prepare-time binding is
   the default; cases 168–170 bind at execute.
4. **Run.** POCO for every case that names no source; the fifteen that name one run against DuckDB
   or PostgreSQL under the profile they name (`PolicyAdoFixture`), where every table is remote so a
   join of two of them pushes as one query.
5. **Compare.** Rows compare as a multiset unless the case says `PolicyCompare.Ordered`, following
   `docs/design/05-testing.md` §5. Decimals compare numerically; the one FP64 case (073) compares
   within 4 ULPs. A value of the form `"!fp:<raw>"` is not a literal: it is the FINGERPRINT of
   `<raw>` under that case's principal `mask_key`, and the run substitutes the reference executor's
   own kernel before comparing.
6. **Assert the acknowledgements**, not only the rows: the per-column `ReportedDisclosure` on the
   prepared query and the `chalk.disclosure` Arrow field metadata against `Expect.Labels`, and
   `EntitlementsReport.tables[]` against `Expect.Report` — `visibility` everywhere, and
   `row_predicate_pushed` on the ADO runs. `RemoteQueryContains` / `RemoteQueryNotContains` in group
   K are substring assertions on the generated SQL, deliberately naming a column rather than a
   spelling, since a one-element `IN` list may simplify to an equality.

## 4. Vocabularies

**`ReportedDisclosure`** (`Expect.Labels`): `Full`, `Masked`, `Redacted`, `PerRow`, `Aggregate` —
the five names of §3.12. The battery was written before D215 renamed `Undisclosed` to `Redacted`;
the cases carry the current name.

**`TableVisibility`** (`Expect.Report[table].Visibility`): `None`, `Some`, `All`.

**`PolicyUse`** on a `POLICY` refusal — the battery's own vocabulary, since the design names the
uses in prose rather than as an enum. It is matched loosely against the message, word by word, and
a miss is reported rather than assumed to be a defect: the mapping onto whatever the implementation
puts in the error is itself the finding.

**`PredicateShape`** on a `PUSHDOWN_REQUIRED` refusal (case 155). The message names the shape the
*host declared and did not* — `PREDICATE_SHAPE_IN`, `PREDICATE_SHAPE_EQ`, … — rather than the
predicate Calcite wrote (F46, ADR 0025 part 2g). Two reasons: `SEARCH` is not a word a host writes
anywhere, and the `Sarg` it wrapped held the principal's own tenancy identifiers, which §3.12 keeps
out of an error message. Where every shape the predicate is made of *is* declared and something else
stopped it — the `max_in_list` ceiling, a string collation, a function — the message says that
instead of guessing a shape.

**Sibling disclosure values** are the *upper-case* strings the Arrow metadata carries — `FULL`,
`MASKED`, `REDACTED`, `AGGREGATE` (D218) — and appear as row literals, not as enum values. The enum
is what the report is checked against; the string is what a `__disclosure` column holds.

## 5. Where the expectation is uncertain, and what was assumed

Everything in this section is a place where a disagreement between this corpus and the reference
executor would be a finding rather than a bug in one of them. They are ordered by how much rides
on them.

### Assumptions about the fixture

**A1 — `orders.org_id` exists.** Design §8 gives `orders` the tenancy through the path
`member.org`, and its own corpus query 02 selects `org_id` from `orders` with no join. A layer-A
row predicate is SQL over *this table's columns and the context*, and §10 puts conditions over
other catalog tables out of scope, so the path cannot be walked in layer A at all. The fixture
therefore carries `org_id` on the row, always equal to the member's org, and the
`orders_ctx_path` variant records the other way a compiler could emit the path: a context list of
member ids the host expanded. No case uses that variant yet; one should.

**A2 — the fixture's own role/realm choices.** Design §8 fixes `manager` Full on pii, `agent` Mask
on pii and NoFull on restricted, `auditor` AggregateOnly on `amount` and the token mask on pii. It
does not say what a *subject* grant discloses, what any role sees on `restricted` besides the
agent, or what an auditor sees on `dob` (whose type the token mask does not fit). This corpus
declares: a subject sees the pii realm in full and `national_id` masked; a manager and a global
grant see `national_id`; everyone else sees NULL for it; the auditor's `dob` is NULL and the
agent's is the year. These are the fixture's choices, written in layer A where they are visible.

**A3 — the case files repeat their SQL.** Nine cases hold `SELECT * FROM members` verbatim,
because the task's shape is one file per case and the design runs each query as every principal.
A kit that would rather key on (statement, principal) can read `file:` as a pointer and dedupe.

### Uncertain expectations

**U1 — a named column whose folded disclosure is a constant NONE.** §3.5's table says a constant
`NONE` collapses the sanitiser to the placeholder; §3.11 says an explicitly named undisclosed
column is "a `POLICY` error when no disclosure could ever permit it, otherwise a placeholder". For
a principal with no grants (u5) *no rule can match*, but another principal's would. Encoded as a
placeholder with the column reported `Undisclosed` (cases 005, 038, 110, 133). If "could ever" is
read per request rather than per descriptor, those become POLICY errors — and a principal with no
grants would then learn from the error text which columns exist.

**U2 — what a derived column over a masked origin reports (case 052).** `COUNT(DISTINCT
first_name)` for an agent is encoded `Full`. §3.12's meet is defined over a column's *origins*, and
a count's origin is a masked column, so `Masked` is the other reading. The same question decides
whether an aggregate over a masked column deserves an acknowledgement at all.

**U3 — two rules of the same disclosure both matching (case 007).** u7 is an agent *and* an
auditor in one org, and both rules say `MASKED` with different masks. First match wins, so the
declaration order in `PolicyEntitlements` decides that u7 sees initials rather than tokens. §5 orders
rules "from most to least permissive", which does not order two rules at the same level.

**U4 — the guard over a mixed principal (case 013).** u12 is a manager in O1 and an auditor in O2.
`SUM(amount) GROUP BY org_id` is permitted, and the corpus expects *both* groups guarded,
including the one where the same principal may read the values one at a time (case 077). That
follows from taint being a property of the column at a leaf rather than of a row, which the design
implies and never says.

**U5 — the remedy of §3.4 (case 077).** The design says `WHERE org_id = 2` makes the folded
disclosure constant `FULL` for a mixed principal. But §3 puts the pass *before* the Hep pre-pass,
and it is the pre-pass that transposes the statement's `WHERE` into `Filter_R`; when the pass folds,
the statement's conjunct is in a filter above `Project_D`. Either the pass gathers the conjuncts
that hold over the leaf from above it, or the remedy does not work as written and case 077 is a
POLICY error. **This one is worth settling before the pass is built.**

**U6 — the vacuous floor (cases 085, 086). Settled.** D211 (owner 2026-09-10) is now in
`docs/design/16-entitlements.md` §3.6, §6 and §11: there is **no shipped floor**. A column's
`min_group_size` of `0` inherits the default the host gives at planning
(`PrepareOptions.DefaultMinGroupSize`, `EntitlementsOptions.default_min_group_size`), which is itself
`0` when the host says nothing; an explicit `1` disables the guard for that column whatever the
default; `2` or more is the floor; and an effective floor of one or less emits **no guard at all** —
no `COUNT(c)`, no guard projection. `CatalogOptions.DefaultMinGroupSize` and the shipped `5` are
gone. Both cases therefore expect no suppression and the reported disclosure `Aggregate`, and the
residual reading the earlier note worried about (a group whose every value is NULL has
`COUNT(c) = 0` under a literal `k = 1`) is closed by the decision rather than by the fixture.

**U7 — where the guard sits relative to `HAVING` (case 087).** §3.6 puts the guard projection
"above the aggregate"; a `HAVING` is also above the aggregate. The corpus assumes the guard is
immediately above, so `HAVING` tests the *guarded* value and a suppressed group drops on UNKNOWN.
The alternative — `HAVING` below the guard — lets a suppressed value decide a filter, which is the
worse of the two, but it is not excluded by the text.

**U8 — `UndisclosedColumns = Refuse` against a named column (case 101).** The option is defined as
what happens to "undisclosed columns *a star surfaces*", and §3.11 refuses a named column only when
no disclosure could ever permit it. Encoded as a placeholder. A host that chose `Refuse` may well
expect the error.

**U9 — `row_predicate_pushed` for a key-set semi-join (case 159). Answered.** §3.7 says a list above
the fold ceiling "is pushed as M5's key set", and it now is (F45, ADR 0025 part 2g): the bound list
drives a lookup join into the entitled scan, whose remote predicate is `"org_id" IN (?)` with a
call's worth of keys bound into that one placeholder. The flag is `true`, as this case encoded it.
The 70 keys take **one** call, because the DuckDB profile's `max_in_list` is 1000; a smaller ceiling
would batch them, and `max_keys_per_call` on the plan's lookup join is what says so.

**U10 — the reported disclosure under `trust_source_row_level_security` (case 157). Settled.** With
`Filter_R` skipped, rows outside the principal's scope come back and every protected column on them
is a placeholder, while rows inside are masked. One origin is therefore both, and §3.12's meet
resolves that to `PerRow`, which is what the case encoded and what the engine now says (F44, ADR
0025 part 2g). The finding turned out to be larger than the label: the pass had been simplifying the
disclosure rules under a row predicate it does **not** emit for a trusted source, which folded the
agent's rule to a constant and gave every row the source returned the mask — so an out-of-scope
row's `first_name` came back as an initial rather than as a placeholder. A trusted leaf is now
simplified over all the rows the source returns. `visibility` stays the folded predicate's own
answer, `Some`, because D156 skips the filter and nothing else.

**U11 — what a k-anonymous raw group key reports (case 160).** Under `statistical` the group key is
the *raw* value, guarded by suppression. None of §3.12's five names is defined for that; `Aggregate`
is encoded, as the name for a value the guard stands behind. `Masked` and `Full` are both
defensible, and a consumer needs to be told which.

**U12 — group suppression on a global aggregate (case 166).** D203 says a group below k is
*dropped*, not NULLed, and injects `HAVING COUNT(*) >= k` on "every `Aggregate` the raw predicate or
key shaped". Applied to `SELECT COUNT(*) FROM members WHERE first_name = 'Petra'` that returns **no
row at all**, which is a shape no SQL consumer expects from a global aggregate. Encoded as written.

**U13 — the scale of `AVG` over `DECIMAL(10,2)` (cases 023, 033, 089, 090).** The values are exact;
the scale of the result is Calcite's division rule and this corpus does not pin it. Compare
numerically. Case 073 (`AVG(CAST(amount AS DOUBLE))`) has no such question. *(089 and 090 also
carried F43's disagreement about `COUNT`'s label, which the F58 run settled in the corpus's favour:
ADR 0038.)*

**U14 — a CAST to a non-numeric type inside a permitted aggregate (case 079).** §3.4 permits "a
bare reference, optionally under a `CAST` to a numeric type"; `COUNT(CAST(amount AS VARCHAR))` is
outside that and harmless. The strict reading is encoded. *(Answered by the F58 run, ADR 0038: the
engine used to accept it because Calcite's converter erased `COUNT`'s argument over a NOT NULL
column. `amount` is nullable in the row type a statement is written over — a rule of it can withhold
the value — so the argument survives, §3.4's trace meets the cast, and the strict reading is what the
engine does.)*

**U15 — what `StarPolicy.RefuseWhenEntitled` names (case 107).** The setting is catalog-wide, so the
refusal is not about the table the star stands over; the case expects that table anyway.

**U16 — the guard inside a decorrelated `LATERAL` (case 115).** The corpus assumes the guard
applies per correlation group after decorrelation, which is what §3.6 says for an `Aggregate`.

**U17 — whether the report simplifies under the statement's own filter (case 138).** `WHERE org_id
= 2` for a mixed principal returns only masked rows; the corpus still reports `PerRow`, because
§3.12 reads the leaf's disclosure map and §3.3 simplifies under `Filter_R`'s conjuncts alone. Same
question as U5, seen from the report's side.

**U18 — `PUSHDOWN_REQUIRED` against a one-element list (case 155). Answered.** A single-org list
does simplify to `org_id = 1`, an `EQ` shape the "no IN" profile declares, so the plan pushed and the
refusal did not fire for u2. Part 2e moved the case to **u11**, an auditor in two organisations, as
this note asked; there the folded predicate is an `IN` the profile does not declare and the refusal
fires. The word for the shape is settled too: the message names `PREDICATE_SHAPE_IN`, which is what
the host declared and did not (F46, §4 above).

**U19 — the reported disclosure under execute-time binding (case 168).** Nothing is folded at
prepare, so a column cannot be a constant `Masked`; `PerRow` may be the honest answer for every
entitled column under that binding. The rows are the assertion that matters and they must equal the
prepare-time ones exactly (case 002).

**U20 — the reported disclosure of a sibling column itself (cases 171–176).** Encoded `Full`: the
name is not the data. The design does not say, and a consumer that walks columns pairwise needs to
know whether a sibling appears in the report at all.

**U21 — the error kind for a suffix collision (case 174).** "Refused at prepare naming the column
and the suffix" does not name a kind. `POLICY` is encoded; `VALIDATION` is defensible.

### Findings — places where the design's §8 table cannot be satisfied as written

**F1 — corpus 10's `visibility = NONE` (cases 029, 030).** §8 expects `SELECT body FROM notes` as
`u5` to report `visibility = NONE` and to be a `POLICY` error under `RefuseWhenNoVisibleRows`. It
cannot be either: the created-by fail-safe (D206) puts `created_by = @ctx.user` in the row
predicate of every table that carries the option, and a comparison against a bound scalar folds to
neither TRUE nor FALSE, so the visibility is `SOME` for *every* principal on such a table. Zero rows
and no error — the rest of §8's expectation — does hold. The corpus puts the refusal on `members`
instead (case 031), which carries no fail-safe. Either §8's corpus 10 should move to a table
without `created_by`, or `visibility` needs a fourth name for "no grant, but the fail-safe may
still match".

**F2 — corpus 10 and corpus 25 cannot both use `u5`.** Corpus 10 needs `u5` to have created
nothing; corpus 25 needs `u5` to have created two notes in O1. The fixture gives `u5` no grants and
no rows at all, and adds `u10`, a principal with the same (empty) grants who created two notes and
two orders, for corpus 25 (cases 039–040, 141–144).

**F3 — corpus 02 selects a column corpus §8's schema does not declare.** See A1.

**F4 — a NULL mask changes a column's nullability.** `national_id` and `dob` are NOT NULL in the
fixture and their masks are NULL, so the output type must be nullable for a masked principal and
not for a manager — while D161 wants "every principal gets the same row shape". §3.11 widens the
type for *placeholders* and says nothing about masks. The corpus expects a nullable output column
for a masked principal (cases 022, 095, 098); if the intent is one row shape for everyone, the
column should be nullable for every principal, which is a decision to record.

**F5 — a constant mask hides NULL-ness (cases 060–062).** `'********'` replaces a NULL note as well
as a value, so `note IS NULL` finds nothing for an agent and one row for a global grant. That is
mechanical, it is probably wanted (the absence of a note is itself information), and a host that
wants NULL preserved must write `CASE WHEN note IS NULL THEN NULL ELSE '********' END` as the mask.
Worth a sentence in the design.

**F6 — the per-column placeholder is hard to reach on a tenanted table.** Every row of `orders` is
visible through some disjunct of the row predicate, and under `CreatorSees = Full` every disjunct
has a matching column rule — so `otherwise: NONE` is unreachable and the declared placeholder is
dead. `orders_none` therefore declares `ByRules`, and case 104 is the only way design corpus 18's
"amount is -1 under either policy" can be observed at all. A compiler that emits a placeholder
should probably warn when no row can reach it.

## 6. What does not run

Case 147 is **layer-B only** — `AllowGlobalGrants` is the tenancy package's check on a *grant*, not
the descriptor's, and this battery writes the entitlements directly, where `global` is an ordinary
context scalar. It is the one case the run leaves out, and `PolicyBatteryCase.LayerBOnly` says so.
Everything else runs, POCO and ADO alike: `PolicyAdoFixture` loads the whole fixture into DuckDB and
into PostgreSQL under the four declared profiles, and the fifteen cases naming one run there (F41).
The PostgreSQL cases skip when there is no server, which `CHALK_TEST_POSTGRES_REQUIRED=1` turns into
a failure.
