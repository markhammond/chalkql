-- Tier 1 widened (D298): a client body over a DATE, written in DateOnly, answering a DATE that the
-- statement groups and orders by.
-- expect: has_user_function(main.week_start)
SELECT week_start(l_shipdate) AS week, COUNT(*) AS lines
FROM lineitem
WHERE l_orderkey < 1000
GROUP BY week_start(l_shipdate)
ORDER BY week
