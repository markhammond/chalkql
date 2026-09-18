-- expect: has(HashJoin)
SELECT b.symbol, b.ts, s.base
FROM bars b
LEFT JOIN (SELECT symbol, base FROM symbols WHERE base IN ('BTC', 'ETH')) s
  ON b.symbol = s.symbol
WHERE b.ts < TIMESTAMP '2026-01-01 00:10:00'
ORDER BY b.ts, b.symbol
