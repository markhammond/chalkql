-- expect: has(MergeJoin)
-- expect: not(HashJoin)
SELECT b.symbol, b.ts, q."close"
FROM bars b JOIN bars q ON b.symbol = q.symbol AND b.ts = q.ts
WHERE b.ts < TIMESTAMP '2026-01-01 01:00:00'
ORDER BY b.ts, b.symbol
