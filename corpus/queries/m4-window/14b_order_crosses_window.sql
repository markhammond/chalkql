-- The same window, ordered the other way round afterwards: the window's own ordering cannot serve
-- (ts, symbol), so this one really sorts.
-- expect: has(Window)
-- expect: has(Sort)
SELECT symbol, ts, SUM(volume) OVER (PARTITION BY symbol ORDER BY ts) AS running_volume
FROM bars_small
ORDER BY ts, symbol
