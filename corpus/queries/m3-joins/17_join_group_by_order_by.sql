-- expect: has(HashJoin)
-- expect: has(HashAggregate)
SELECT s.base, COUNT(*) AS n
FROM bars b JOIN symbols s ON b.symbol = s.symbol
WHERE b.ts < TIMESTAMP '2026-01-01 01:00:00'
GROUP BY s.base
ORDER BY 2 DESC, 1
