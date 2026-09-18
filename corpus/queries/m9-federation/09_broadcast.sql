-- §6's fourth strategy, chosen by cost rather than by decree. `BROADCAST` ships the small side's
-- rows into the source's own query as a `VALUES` relation and asks **once**, where `LOOKUP` asks
-- once per batch of at most `max_in_list` keys. SQLite's descriptor declares a ceiling of 200, so
-- fifteen hundred customer keys would cost eight calls; shipping them costs one call and fifteen
-- hundred values, and the cost model prefers it without being told to.
--
-- Broadcast is offered only when the small side's estimate is a measured statistic: shipping a side
-- whose size is a guess is the bet D97 exists to avoid, and a guessed estimate gets an adaptive join
-- instead.
-- expect: has(LookupJoin)
-- expect: has(RemoteQuery)
SELECT c.c_name, o.o_orderkey
FROM customer c JOIN sqlite.orders o ON o.o_custkey = c.c_custkey
WHERE o.o_orderkey <= 800
ORDER BY o.o_orderkey, c.c_custkey
