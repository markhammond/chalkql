-- expect: not(HashAggregate)
-- expect: not(Aggregate)
SELECT DISTINCT ts, symbol FROM bars
