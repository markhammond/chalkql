-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts,
       SUM(volume) OVER (PARTITION BY symbol ORDER BY ts ROWS 2 PRECEDING EXCLUDE CURRENT ROW) AS s
FROM bars_small
