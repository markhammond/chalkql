-- §6 corpus 02, second half. The same shape against a source whose declared `max_in_list` is 200,
-- under a request policy that will pay for exactly one call: 200 × 1 is below the two hundred and
-- ten distinct keys the small side turns out to have, so the adaptive join takes its **local**
-- branch instead. Same rows, different counters, and the decision recorded — which is the whole
-- point of deciding at execution rather than from an estimate.
-- join-policy: lookup_max_calls=1
-- expect: has(AdaptiveJoin)
-- expect: has(RemoteQuery)
SELECT c.c_name, o.o_orderkey
FROM customer c JOIN sqlite.orders o ON o.o_custkey = c.c_custkey
WHERE c.c_mktsegment = 'BUILDING' AND o.o_orderkey <= 2000
ORDER BY o.o_orderkey, c.c_custkey
