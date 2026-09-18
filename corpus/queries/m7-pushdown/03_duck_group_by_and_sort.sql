-- §5 corpus 03. The aggregate and the sort both push; one row per flag comes back.
-- expect: has(RemoteQuery)
-- expect: not(HashAggregate)
-- expect: not(Sort)
SELECT l_returnflag, SUM(l_quantity) AS total
FROM duck.lineitem
GROUP BY l_returnflag
ORDER BY l_returnflag
