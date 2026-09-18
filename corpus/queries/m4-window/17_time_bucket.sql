-- expect: has(HashAggregate)
-- expect: has_function(TIME_BUCKET)
-- expect: not(Window)
SELECT symbol, TIME_BUCKET(INTERVAL '5' MINUTE, ts) AS bucket, SUM(volume) AS v
FROM bars_small
GROUP BY symbol, TIME_BUCKET(INTERVAL '5' MINUTE, ts)
ORDER BY symbol, bucket
