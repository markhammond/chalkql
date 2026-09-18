-- D54 again, with two distinct columns and a much larger group count; which strategy the cost model
-- picks depends on the catalog's distinct counts and is recorded in ADR 0017.
-- expect: has(HashAggregate)
SELECT l_orderkey,
       COUNT(DISTINCT l_partkey) AS parts,
       COUNT(DISTINCT l_suppkey) AS suppliers,
       SUM(l_quantity) AS quantity
FROM lineitem
GROUP BY l_orderkey
ORDER BY l_orderkey
