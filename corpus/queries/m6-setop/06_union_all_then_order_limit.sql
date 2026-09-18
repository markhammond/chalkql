-- D69: a set operation delivers no ordering, so the ORDER BY above it is a real sort — the plan
-- says so rather than claiming the inputs' order survived.
-- expect: has(TopN)
-- expect: has(SetOp)
SELECT symbol, ts FROM bars_small WHERE symbol = 'BTCUSDT'
UNION ALL
SELECT symbol, ts FROM bars_small WHERE symbol = 'ETHUSDT'
ORDER BY ts, symbol
LIMIT 10
