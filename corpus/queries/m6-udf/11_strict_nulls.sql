-- §5 corpus 11. STRICT means NULL in, NULL out without the body running: the inlined one answers
-- NULL through its guard, and the client-bodied one never sees the lane.
-- expect: has_user_function(main.bucket_price)
SELECT pct_change(NULL, 1.0) AS a, bucket_price(NULL, 5.0) AS b
FROM bars_small
LIMIT 1
