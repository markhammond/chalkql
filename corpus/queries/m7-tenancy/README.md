# `m7-tenancy` — the entitlement corpus

Every query here is run **as every principal** (`docs/design/16-entitlements.md` §8), which is what
makes it a corpus rather than a set of cases: a policy's behaviour is a function of the principal,
and a refusal shows for one and not another. The principals are `Chalk.TestKit.TenancyFixture`'s —
`u1` a manager in O1 and an agent in O2, `u2` an agent in O1, `u3` a confined subject grant and
`u3-in-o1` the same grant confined to the other organisation (§8's query 24), `u4` a global grant,
`u5` no grants at all and the creator of the two notes of query 25, `u6` an auditor in O1,
`u6-two-orgs` an auditor in both (query 13's group below the floor), `u7` the host-computed list of
query 15b.

The tables are `orgs`, `members`, `orders`, `notes`, `invites`, `symbols` and — from §3.13 —
`threads`, `messages` and `attachments`, the last two carrying no tenancy column at all.

**What is checked, per query and per principal** (`TenancyCorpusTests`):

1. The rows match the **oracle** — `Chalk.TestKit.TenancyOracle`, which states §8's model in C# and
   discloses the rows directly, with no descriptor, no planner and no pass, then runs the statement
   over an ordinary unentitled source. This is §9's first exit criterion. An oracle catches the
   misclassification nobody thought of; a hand-written expectation catches the one its author did.
2. The rows and the per-column report match a **recorded golden** under `corpus/plans/m7-tenancy/`,
   so a change in what a principal is told is a diff a reviewer reads. Regenerate with
   `CHALK_WRITE_FIXTURES=1` and read it.

**The headers.** `-- expect: principals(all)` says the query runs as every principal;
`-- expect: policy(<principal>)` names a principal for whom the statement is a `POLICY` refusal, and
the golden records the refusal's first sentence; `-- expect: guarded` marks a statement whose
aggregate the group-size guard rewrites, and those are compared with the golden alone — suppression
below the floor is a property of the *statement's* shape rather than of a row, and an oracle that
took a statement apart to find the guarded calls would be the pass written a second time.

**Through a parent (§3.13).** Queries 26–28 are the family a table with *no tenancy column of its
own* adds: `messages` is entitled through `threads` and `attachments` through `messages`, so which
rows come back and what `content` discloses are both decided on the parent's side of a join the
statement never wrote. Read the goldens per principal and the mechanism is visible in one page — u1
manages O1 and acts as an agent in O2, so O1's messages come back whole and O2's as an excerpt; u3
holds a subject grant on the member thread 3 is about and sees that thread's message in full; u4
holds the global grant, and its report names no `threads` at all, because the parent's predicate
folded to TRUE over a key every message carries and the join was left out of the plan; u5 holds
nothing and the derived visibility is NONE. The shapes a golden cannot record as rows — two parents
OR-ed, one parent through two columns, a nullable correlation key and execute-time binding — are
`Chalk.Integration.Tests.ThroughTests`, and the registration refusals are
`chalk.planner.ThroughRegistrationTest` and `Chalk.Catalog.Tests.ParentVisibilityTests`.

**The test verdict (D261, §36).** Queries 29–34 are the family a column the principal may *test and
never read* adds. `national_id` is disclosed to nobody, and two roles reach it another way: `u8` is a
support desk that may confirm an identifier — `=`, `<>` and `IN (list)` against a parameter, each
computed in the leaf over the raw value and handed back as one boolean — and `u9` may count matches,
guarded by the floor of three. Read the goldens per principal and the whole of it is on one page:
`u8` gets `1|True` and `2|False` where every other principal gets the placeholder compared, which is
NULL compared, which is NULL; `u9`'s grouped count withholds O1's group of two and discloses O2's
group of three; and `u9` is refused the six statements that read `national_id` as a *value*, because
a column a rule can restrict to a population is population-only for every use (§3.4) — which is the
same answer `orders.amount` gives an auditor, on the column D261 added a second role to.

`-- expect: tested` marks the six, and they are compared with their goldens alone for the reason
`guarded` is: the oracle discloses a *source*, and a column that is testable and not readable is not
a value a source can hold. The naive model of §36.4 — the raw value compared with the parameter,
three-valued — is `TenancyOracle.Tested` and `.Counted`, and
`Chalk.Integration.Tests.TestVerdictTests` holds the engine to it for every principal and every
shape (ADR 0042). The disallowed shapes over the same column are the adversarial family's 41–49.

**Conjoined confinement (D266, §40).** Queries 35–37 are what a grant confined along *several*
tenancy kinds at once does to a statement. The fixture gains `regions(id, name)` and
`orders.region_id`, and two principals hold one grant apiece with the conjunction inside it:
`u10-in-a-region` audits O1 **confined to region 2**, and `u11-subject-in-a-region` holds a subject
grant on member 1 confined to O1 **and** region 1. Neither is the union of two grants, which is what
holding two would have given: 35 shows u10 with the two O1 orders that lie in R2 and neither the O1
orders in R1 nor any other organisation's order in R2, and u11 with one of member 1's two orders.
36 is the same seen from an aggregate — whole regions missing rather than rows missing from a
region. 37 is §40.3's own rule: a member's row carries no region at all, so the confining kind
resolves nothing there, the grant reaches no member, and the join loses every row while the
principals whose grants confine nothing are unchanged. Query 03's star refuses for u10 for the
reason it already refused for u6: `amount` is population-only for the auditor role, whichever region
it is confined to. The adversarial family's 50–52 are the other side of the same subject.

**What is not here.** §8's queries 16 and 21 need a real database and live in
`Chalk.Integration.Tests.EntitlementsAdoTests`; 17, 18 and 23 are about a prepare option or a
registration refusal rather than about rows, and live in `EntitlementsTests`. The numbering in each
file's first line is §8's, so a reader can compare.
