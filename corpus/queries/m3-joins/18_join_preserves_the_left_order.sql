-- expect: has(HashJoin)
-- expect: not(Sort)
-- expect: not(TopN)
SELECT b.symbol, b.ts, s.base
FROM bars b JOIN symbols s ON b.symbol = s.symbol
WHERE b.ts < TIMESTAMP '2026-01-01 01:00:00'
ORDER BY b.ts
