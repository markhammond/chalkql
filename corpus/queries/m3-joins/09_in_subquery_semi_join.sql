-- expect: join_type=NestedLoopJoin:SEMI
SELECT symbol, ts
FROM bars
WHERE symbol IN (SELECT symbol FROM symbols WHERE quote = 'USDT')
  AND ts < TIMESTAMP '2026-01-01 00:10:00'
ORDER BY ts, symbol
