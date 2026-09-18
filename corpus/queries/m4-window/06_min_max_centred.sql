-- expect: has(Window)
-- expect: not(Sort)
SELECT symbol, ts,
       MIN(low) OVER (PARTITION BY symbol ORDER BY ts ROWS BETWEEN 4 PRECEDING AND 4 FOLLOWING) AS lo,
       MAX(high) OVER (PARTITION BY symbol ORDER BY ts ROWS BETWEEN 4 PRECEDING AND 4 FOLLOWING) AS hi
FROM bars_small
