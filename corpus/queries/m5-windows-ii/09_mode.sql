-- D57: MODE, with ties broken by the smallest value.
-- expect: has(HashAggregate)
SELECT symbol, MODE(trade_count) AS busiest
FROM bars_small
GROUP BY symbol
ORDER BY symbol
