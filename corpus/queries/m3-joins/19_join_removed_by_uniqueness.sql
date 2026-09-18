-- F16(d): the LEFT join reads no `customer` column and joins on `c_custkey`, which `customer` is
-- unique in, so PROJECT_JOIN_REMOVE deletes it. The results must be the orders table's own rows,
-- which is what the reference executor checks.
-- expect: not(HashJoin)
-- expect: not(MergeJoin)
-- expect: not(NestedLoopJoin)
SELECT o.o_orderkey, o.o_totalprice
FROM orders o LEFT JOIN customer c ON o.o_custkey = c.c_custkey
WHERE o.o_orderkey < 200
ORDER BY o.o_orderkey
