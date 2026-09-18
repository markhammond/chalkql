-- D59: the segment tree. An EXCLUDE cuts one contiguous interval out of the frame.
-- expect: has(Window)
SELECT symbol, ts,
       SUM(volume) OVER (
         PARTITION BY symbol ORDER BY ts
         ROWS BETWEEN 5 PRECEDING AND 5 FOLLOWING EXCLUDE GROUP) AS around
FROM bars_small
ORDER BY symbol, ts
