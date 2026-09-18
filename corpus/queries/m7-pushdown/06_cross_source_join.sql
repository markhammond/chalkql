-- §5 corpus 06. `duck.orders` and the local POCO `customer` are different sources, so neither
-- convention can hold the join. M4 left it a local hash join over two full fetches; M5 gives it a
-- strategy (D103): the smaller side drives, and `orders` is asked for its keys rather than read
-- whole. `SourceJoin` is still absent, which is the part that has not changed.
-- expect: has(RemoteQuery)
-- expect: has(LookupJoin)
-- expect: not(HashJoin)
SELECT o.o_orderkey, c.c_name
FROM duck.orders o JOIN main.customer c ON o.o_custkey = c.c_custkey
WHERE o.o_orderkey < 200
ORDER BY o.o_orderkey
