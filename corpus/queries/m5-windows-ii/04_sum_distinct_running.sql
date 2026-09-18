-- D56, cumulative: the lower bound never moves, so nothing is ever removed from the multiset.
-- expect: has(Window)
SELECT symbol, ts,
       SUM(DISTINCT volume) OVER (PARTITION BY symbol ORDER BY ts) AS distinct_volume
FROM bars_small
ORDER BY symbol, ts
