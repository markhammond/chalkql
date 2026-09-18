-- D57: the two percentiles over a GROUP BY.
-- expect: has(HashAggregate)
SELECT symbol,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY "close") AS median,
       PERCENTILE_DISC(0.9) WITHIN GROUP (ORDER BY "close") AS p90
FROM bars_small
GROUP BY symbol
ORDER BY symbol
