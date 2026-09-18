-- expect: has(HashAggregate)
-- expect: join_type=NestedLoopJoin:SEMI
SELECT symbol, ts, "close"
FROM bars
WHERE "close" > (SELECT AVG("close") FROM bars)
  AND ts < TIMESTAMP '2026-01-01 00:10:00'
ORDER BY ts, symbol
