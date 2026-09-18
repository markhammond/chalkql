-- §6 corpus 03. Both sides are remote and neither convention can hold the join, so cost chooses
-- which one is asked: the smaller side drives and the larger is looked up.
-- expect: has(RemoteQuery)
-- expect: count(RemoteQuery)=2
SELECT c.c_name, o.o_orderkey, o.o_totalprice
FROM duck.customer c JOIN sqlite.orders o ON o.o_custkey = c.c_custkey
WHERE c.c_custkey <= 40
ORDER BY o.o_orderkey
