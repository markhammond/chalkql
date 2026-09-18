-- lineitem declares (l_orderkey, l_linenumber), which is exactly what the window asks for.
-- expect: has(Window)
-- expect: not(Sort)
-- expect: not(IndexLookup)
SELECT l_orderkey, l_linenumber,
       SUM(l_quantity) OVER (PARTITION BY l_orderkey ORDER BY l_linenumber) AS running_quantity
FROM lineitem
