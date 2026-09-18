-- D55: three overlapping 15-minute windows per row, sliding every 5 minutes.
-- expect: has(Hop)
SELECT symbol, window_start, SUM(volume) AS volume
FROM TABLE(HOP(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '5' MINUTE, INTERVAL '15' MINUTE))
GROUP BY symbol, window_start
ORDER BY symbol, window_start
