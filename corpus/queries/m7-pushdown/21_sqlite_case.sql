-- D273, F104: query 20's statement against the other in-process dialect, so the text SQLite is
-- sent is a golden of its own. `CASE` is SQL-92 and SQLite has spelled it since 3.0, which is the
-- whole argument for the capability defaulting to true — but "every dialect spells it the same way"
-- is a claim, and a recorded text per dialect is what makes it one a reviewer can check rather than
-- one the design asserts.
-- expect: has(RemoteQuery)
-- expect: not(Project)
-- expect: not(Filter)
SELECT o_orderkey,
       CASE WHEN o_custkey > 100 THEN 3 WHEN o_custkey > 50 THEN 2 ELSE 1 END AS band,
       CASE o_shippriority WHEN 0 THEN 10 WHEN 1 THEN 20 ELSE 30 END AS priority_code,
       CASE WHEN o_custkey > 120 THEN o_custkey END AS big_customer,
       CASE WHEN o_custkey > 130 THEN NULL ELSE o_orderkey END AS small_order
FROM sqlite.orders
WHERE CASE WHEN o_shippriority > 0 THEN o_custkey ELSE o_orderkey END > 140
ORDER BY o_orderkey
