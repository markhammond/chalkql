-- D69: UNION ALL is pure pass-through, which is why it is this step's own allocation gate (§5).
-- Both branches are equality lookups on the (symbol, ts) index, so the plan is two IndexLookups
-- under one SetOp.
-- expect: has(SetOp)
-- expect: has(IndexLookup)
SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'
UNION ALL
SELECT symbol, ts FROM bars WHERE symbol = 'ETHUSDT'
ORDER BY symbol, ts
