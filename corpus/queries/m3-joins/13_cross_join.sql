-- expect: has(NestedLoopJoin)
-- expect: has(TopN)
SELECT s.symbol, r.r_name
FROM symbols s CROSS JOIN region r
ORDER BY s.symbol, r.r_name
LIMIT 7
