-- §5 corpus 05, grouped half. One struct state per group, in an arena-owned array.
-- expect: has(HashAggregate)
-- expect: has_user_function(main.geo_mean)
SELECT symbol, geo_mean("close") AS g
FROM bars_small
GROUP BY symbol
ORDER BY symbol
