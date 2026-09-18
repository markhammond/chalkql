-- expect: has(HashAggregate)
-- expect: has(Filter)
SELECT symbol, SUM(volume) AS vol FROM bars GROUP BY symbol HAVING SUM(volume) > 1000000
