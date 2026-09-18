-- Design 39 §6. PIVOT, in process. No PIVOT statement existed in this corpus: Calcite parses it in
-- the core grammar and rewrites it to filtered aggregates over the pivot column, which the executor
-- already runs, and this is the case that says so. The rewrite is one HashAggregate whose measures
-- carry FILTER, and a Project that restores NULL where a group had no rows at all.
-- expect: has(HashAggregate)
-- expect: not(RemoteQuery)
SELECT *
FROM (SELECT o_orderpriority, o_orderstatus, o_totalprice FROM orders)
PIVOT (SUM(o_totalprice) AS total, COUNT(*) AS n
       FOR o_orderstatus IN ('O' AS unfilled, 'F' AS filled, 'P' AS pending))
ORDER BY o_orderpriority
