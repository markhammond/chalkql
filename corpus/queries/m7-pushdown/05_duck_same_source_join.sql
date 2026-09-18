-- §5 corpus 05. Both tables belong to `duck`, so the join is one query.
-- expect: has(RemoteQuery)
-- expect: not(HashJoin)
-- expect: not(NestedLoopJoin)
SELECT o.o_orderkey, o.o_totalprice, c.c_name
FROM duck.orders o JOIN duck.customer c ON o.o_custkey = c.c_custkey
WHERE c.c_mktsegment = 'BUILDING' AND o.o_orderkey < 500
ORDER BY o.o_orderkey
