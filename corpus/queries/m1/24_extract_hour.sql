-- expect: has(HashAggregate)
-- expect: has_function(EXTRACT)
SELECT EXTRACT(HOUR FROM ts) AS h, AVG("close") AS c FROM bars GROUP BY EXTRACT(HOUR FROM ts) ORDER BY h
