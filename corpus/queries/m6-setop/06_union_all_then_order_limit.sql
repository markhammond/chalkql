-- D69: a set operation delivers no ordering, so the ORDER BY above it is a real sort — the plan
-- says so rather than claiming the inputs' order survived.
-- D276: and the bound above it is copied into every branch, so each contributes only its first
-- ten candidates and the global heap picks from twenty rows rather than from both tables.
-- expect: has(TopN)
-- expect: has(SetOp)
-- expect: count(TopN)=3
SELECT symbol, ts FROM bars_small WHERE symbol = 'BTCUSDT'
UNION ALL
SELECT symbol, ts FROM bars_small WHERE symbol = 'ETHUSDT'
ORDER BY ts, symbol
LIMIT 10
