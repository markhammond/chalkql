-- D69: the branches' types differ — trade_count is I32 and volume is I64 — so Calcite inserts the
-- casts that make the row types identical and the result is I64.
-- expect: has(SetOp)
-- expect: has(Project)
SELECT symbol, trade_count AS n FROM bars_small WHERE symbol = 'BTCUSDT'
UNION ALL
SELECT symbol, volume AS n FROM bars_small WHERE symbol = 'ETHUSDT'
ORDER BY symbol, n
