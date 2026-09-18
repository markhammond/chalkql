-- Design 39 §6. The same PIVOT over the DuckDB copy. The rewrite is filtered aggregates, DuckDB's
-- profile declares FILTER, and so the whole pivot leaves as one remote query — the pivot is not a
-- thing the client does to a scan, it is a shape the source can be asked for.
-- expect: has(RemoteQuery)
-- expect: count(RemoteQuery)=1
-- expect: not(HashAggregate)
SELECT *
FROM (SELECT o_orderpriority, o_orderstatus, o_totalprice FROM duck.orders)
PIVOT (SUM(o_totalprice) AS total, COUNT(*) AS n
       FOR o_orderstatus IN ('O' AS unfilled, 'F' AS filled, 'P' AS pending))
ORDER BY o_orderpriority
