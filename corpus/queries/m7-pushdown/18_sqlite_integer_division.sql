-- F27 (ADR 0027), 17 on the dialect where nothing is substituted. SQLite's `/` over two integers
-- already is integer division truncating towards zero -- measured on the pinned e_sqlite3 3.53.3,
-- `-7 / 3` is -2 in an integer type -- so the dialect writes the call through and the golden is
-- what an unsubstituted integer division looks like. Reading 17 and 18 side by side is the point
-- of having both, the same way 14 and 15 are a pair for MOD.
--
-- It is here for the record rather than because anything was wrong here: this query answered
-- correctly on SQLite before the hotfix and answers identically after it, which is half of what
-- "every other dialect keeps `/`" has to mean.
-- expect: has(RemoteQuery)
-- expect: not(Project)
-- expect: not(Filter)
SELECT o_orderkey,
       o_custkey,
       o_custkey / 3 AS third,
       (0 - o_custkey) / 3 AS negative_third,
       o_custkey / 3 + 1 AS third_then_plus,
       10 - o_custkey / 3 AS third_on_the_right,
       (o_custkey + 1) / 3 AS sum_then_third,
       CAST(o_custkey AS DOUBLE) / 3 AS real_third
FROM sqlite.orders
WHERE o_custkey / 3 = 2
ORDER BY o_orderkey
