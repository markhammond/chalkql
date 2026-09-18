-- expect: has(HashAggregate)
-- expect: has_function(FLOOR_TEMPORAL)
SELECT symbol, FLOOR(ts TO HOUR) AS hr, SUM(volume) AS vol
FROM bars GROUP BY symbol, FLOOR(ts TO HOUR) ORDER BY symbol, hr
