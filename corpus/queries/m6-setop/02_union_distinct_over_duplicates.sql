-- D69: UNION with a third branch that overlaps the first, so the duplicates are real and the
-- DISTINCT has something to remove.
-- expect: has(SetOp)
SELECT symbol FROM bars_small WHERE symbol = 'BTCUSDT'
UNION
SELECT symbol FROM bars_small WHERE symbol = 'ETHUSDT'
UNION
SELECT symbol FROM bars_small WHERE symbol IN ('BTCUSDT', 'SOLUSDT')
ORDER BY symbol
