-- expect: has(HashAggregate)
-- expect: join_type=NestedLoopJoin:LEFT
SELECT b.symbol, b.ts
FROM bars b
WHERE NOT EXISTS (SELECT 1 FROM events e WHERE e.symbol = b.symbol AND e.kind = 'listing')
  AND b.ts < TIMESTAMP '2026-01-01 00:10:00'
ORDER BY b.ts, b.symbol
