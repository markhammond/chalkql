-- §6 corpus 12. A correlated `NOT EXISTS` whose sub-query is in another source. Decorrelation turns
-- it into an anti join (D67), and the anti join is a lookup: there is no `Correlate` in the IR at
-- all, and no nested loop with a remote query on its inner side.
-- expect: not(Correlate)
-- expect: not(NestedLoopJoin)
-- expect: has(RemoteQuery)
SELECT c.c_custkey, c.c_name
FROM customer c
WHERE c.c_custkey <= 50
  AND NOT EXISTS (SELECT 1 FROM duck.orders o WHERE o.o_custkey = c.c_custkey)
ORDER BY c.c_custkey
