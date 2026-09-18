-- expect: has(NestedLoopJoin)
-- expect: not(HashJoin)
SELECT b.symbol, b.ts, e.id, e.kind
FROM bars b JOIN events e ON b.ts BETWEEN e.start_ts AND e.end_ts
WHERE b.ts < TIMESTAMP '2026-01-01 03:00:00'
ORDER BY b.ts, b.symbol, e.id
