-- The window already delivers (symbol, ts), so the ORDER BY costs nothing.
-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts) AS running_volume
FROM bars_small
ORDER BY symbol, ts
