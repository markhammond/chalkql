-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts,
       "close" / LAG("close", 1) OVER (PARTITION BY symbol ORDER BY ts) - 1 AS ret,
       LEAD("close", 1) OVER (PARTITION BY symbol ORDER BY ts) AS next_close
FROM bars_small
