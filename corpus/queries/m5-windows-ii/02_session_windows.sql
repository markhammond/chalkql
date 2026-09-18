-- D55. trades_sparse declares (symbol, ts), which is exactly the order a session window requires and
-- also the order it delivers, so this plans with no Sort at all. The partition key is a second
-- DESCRIPTOR, not a PARTITION BY clause: Calcite 1.42 refuses PARTITION BY on a window table
-- function (V24, ADR 0018).
-- expect: has(Session)
-- expect: not(Sort)
SELECT symbol, ts, window_start, window_end
FROM TABLE(SESSION(TABLE trades_sparse, DESCRIPTOR(ts), DESCRIPTOR(symbol), INTERVAL '2' HOUR))
ORDER BY symbol, ts
