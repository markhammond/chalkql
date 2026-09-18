-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts, vwap,
       LAG(vwap, 1) IGNORE NULLS OVER (PARTITION BY symbol ORDER BY ts) AS previous_vwap
FROM bars_small
