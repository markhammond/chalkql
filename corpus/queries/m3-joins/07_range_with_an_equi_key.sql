-- expect: has(HashJoin)
SELECT b.symbol, b.ts, e.kind
FROM bars b JOIN events e
  ON b.symbol = e.symbol AND b.ts BETWEEN e.start_ts AND e.end_ts
WHERE b.ts < TIMESTAMP '2026-01-01 06:00:00'
ORDER BY b.ts, b.symbol, e.kind
