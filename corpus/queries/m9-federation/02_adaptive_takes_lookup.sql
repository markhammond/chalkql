-- §6 corpus 02, first half. The local side is filtered, so its estimate is a guess rather than a
-- measured statistic — which is exactly when the planner emits an `AdaptiveJoin` (D97). At
-- execution the keys are counted, they fit in one call, and the lookup branch is taken; the
-- decision is in `Stats.AdaptiveDecisions`. The local side is narrowed on a column the join does
-- not mention, so the predicate cannot travel with the key and turn the lookup into a filter the
-- source was going to apply anyway.
-- expect: has(AdaptiveJoin)
-- expect: has(RemoteQuery)
SELECT c.c_name, o.o_orderkey, o.o_totalprice
FROM customer c JOIN duck.orders o ON o.o_custkey = c.c_custkey
WHERE c.c_mktsegment = 'BUILDING' AND o.o_orderkey <= 3000
ORDER BY o.o_orderkey, c.c_custkey
