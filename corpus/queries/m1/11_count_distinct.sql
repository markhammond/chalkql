-- expect: has(HashAggregate)
SELECT COUNT(DISTINCT symbol) AS n FROM bars
