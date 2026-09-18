-- expect: has(HashAggregate)
-- expect: group_keys=0
SELECT COUNT(*) AS total, COUNT(vwap) AS with_vwap, SUM(vwap) AS sum_vwap FROM bars
