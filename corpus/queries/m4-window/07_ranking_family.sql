-- The hour of the bar is a deliberately low-cardinality order key, so every peer group has ties and
-- RANK, DENSE_RANK, PERCENT_RANK and CUME_DIST all have to agree about them. NTILE is the one
-- ranking function ties make non-deterministic, so it gets the total order (symbol, ts).
-- expect: has(Window)
-- expect: count(Window)=2
SELECT symbol, ts,
       RANK() OVER h AS r,
       DENSE_RANK() OVER h AS dr,
       PERCENT_RANK() OVER h AS pr,
       CUME_DIST() OVER h AS cd,
       NTILE(4) OVER (PARTITION BY symbol ORDER BY ts) AS quartile
FROM bars_small
WINDOW h AS (PARTITION BY symbol ORDER BY EXTRACT(HOUR FROM ts))
ORDER BY symbol, ts
