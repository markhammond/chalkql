-- The shape a host actually writes: one row per session. The sort above the aggregate is the
-- ORDER BY's, not the session's — query 02 is where the "no Sort" claim is made.
-- expect: has(Session)
SELECT symbol, window_start, window_end, COUNT(*) AS trades
FROM TABLE(SESSION(TABLE trades_sparse, DESCRIPTOR(ts), DESCRIPTOR(symbol), INTERVAL '2' HOUR))
GROUP BY symbol, window_start, window_end
ORDER BY symbol, window_start
