-- Composition: a hop feeding a join and then a window.
-- expect: has(Hop)
SELECT h.symbol, h.window_start, s.base,
       SUM(h.volume) OVER (PARTITION BY h.symbol ORDER BY h.window_start) AS running_volume
FROM TABLE(HOP(TABLE bars_small, DESCRIPTOR(ts), INTERVAL '10' MINUTE, INTERVAL '20' MINUTE)) h
JOIN symbols s ON s.symbol = h.symbol
ORDER BY h.symbol, h.window_start, h.ts
