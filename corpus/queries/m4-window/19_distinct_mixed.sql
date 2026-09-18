-- D54: a DISTINCT aggregate beside a plain one, which M1 could not plan at all.
-- expect: has(HashAggregate)
SELECT symbol, COUNT(DISTINCT trade_count) AS distinct_trade_counts, SUM(volume) AS v
FROM bars
GROUP BY symbol
ORDER BY symbol
