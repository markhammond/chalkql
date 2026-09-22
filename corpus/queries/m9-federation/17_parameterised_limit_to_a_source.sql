-- §6 corpus 17. A bound that is a parameter travels to the source exactly where a literal one
-- does. The text the source is sent carries a placeholder where the count goes, and the executor
-- writes the number this execution bound into it before the query is made — so the source truncates
-- and only the rows that were asked for cross the boundary. The unpushed plan answers the same rows
-- from the same source, which is what the level sweep compares. The predicate is a numeric one so
-- that the PostgreSQL twin of this query pushes it too: a string comparison there is the locale's
-- and not Chalk's, and the gate keeps the whole subtree local for it.
-- expect: has(RemoteQuery)
-- expect: count(RemoteQuery)=1
SELECT o_orderkey, o_totalprice
FROM duck.orders
WHERE o_totalprice > 100000
ORDER BY o_orderkey
LIMIT ?
