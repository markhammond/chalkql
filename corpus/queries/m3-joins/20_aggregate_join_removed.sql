-- The same removal under an aggregate: AGGREGATE_JOIN_REMOVE. Grouping over the child alone must
-- give the same counts as grouping over the join, because the join multiplies nothing.
-- expect: not(HashJoin)
-- expect: not(MergeJoin)
-- expect: not(NestedLoopJoin)
-- expect: has(HashAggregate)
SELECT o.o_orderstatus, COUNT(*) AS n
FROM orders o LEFT JOIN customer c ON o.o_custkey = c.c_custkey
GROUP BY o.o_orderstatus
ORDER BY o.o_orderstatus
