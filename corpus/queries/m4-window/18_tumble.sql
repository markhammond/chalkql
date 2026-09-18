-- Calcite's TUMBLE table function, rewritten into the same projection query 17 writes by hand (D52).
-- expect: has(HashAggregate)
-- expect: has_function(TIME_BUCKET)
-- expect: not(Window)
SELECT symbol, window_start, SUM(volume) AS v
FROM TABLE(TUMBLE(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '5' MINUTE))
GROUP BY symbol, window_start
ORDER BY symbol, window_start
