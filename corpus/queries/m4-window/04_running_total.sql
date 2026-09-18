-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts) AS running_volume
FROM bars_small
