-- D273, F104. A statement's own conditional, pushed. Until this capability a `CASE` was never
-- pushed to a SQL source and a host could not enable it: `IfThen` is a top-level `Expr` kind rather
-- than a `FunctionId`, so it could not be named in `PushableFunctions`, and a projection carrying
-- one stayed above the boundary even where every dialect spells `CASE` the same way. It is now on
-- unless a source says otherwise (`supports_case`, absent means true).
--
-- Four shapes in one statement, because each is written differently and a converter can lose any
-- of them on its own. A **searched** conditional with two conditions and an `ELSE`; a **simple**
-- one, which Calcite expands into a searched one over equalities before the gate ever sees it, so
-- the golden is where a reviewer sees that happen; one with **no `ELSE`**, whose value is NULL for
-- every row matching no arm; and one whose `THEN` is itself NULL, which is the shape a sanitiser's
-- withheld branch has. The last two together are why the goldens are worth reading rather than
-- trusting: a NULL arm is where a dialect's type inference shows, and `ELSE NULL` is written out.
--
-- Integer columns and integer arms throughout, and that is not tidiness. PostgreSQL's shipped
-- profile declares `StringCollation.Locale` — a cluster's default collation is its locale's and
-- only `C` is binary — so a string comparison is not pushable there at all, and a conditional
-- holding one would have stayed local on the one dialect nobody here runs in process, which is the
-- leg this query exists to record. Nothing about the conditional needs a string to show it.
--
-- The conditional is in the `WHERE` too, and that is the half that fails silently: a projection
-- that came back wrong is a wrong column a differential sees immediately, while a predicate the
-- source evaluated differently returns plausible rows and nothing catches it but the reference
-- executor. What the goldens show is that Calcite's simplifier reaches a predicate-side conditional
-- first and rewrites it into the disjunction it stands for, so what the source is sent is
-- comparisons rather than a `CASE` — a fact about the simplifier, recorded here rather than
-- assumed.
--
-- `not(Project)` and `not(Filter)` are the claim that the whole expression list and the whole
-- predicate went to the source. o_orderkey is projected because the ORDER BY needs it.
-- expect: has(RemoteQuery)
-- expect: not(Project)
-- expect: not(Filter)
SELECT o_orderkey,
       CASE WHEN o_custkey > 100 THEN 3 WHEN o_custkey > 50 THEN 2 ELSE 1 END AS band,
       CASE o_shippriority WHEN 0 THEN 10 WHEN 1 THEN 20 ELSE 30 END AS priority_code,
       CASE WHEN o_custkey > 120 THEN o_custkey END AS big_customer,
       CASE WHEN o_custkey > 130 THEN NULL ELSE o_orderkey END AS small_order
FROM duck.orders
WHERE CASE WHEN o_shippriority > 0 THEN o_custkey ELSE o_orderkey END > 140
ORDER BY o_orderkey
