-- expect: has(HashAggregate)
SELECT symbol, COUNT(*) AS n, SUM(volume) AS vol, MIN(low) AS lo, MAX(high) AS hi
FROM bars GROUP BY symbol
