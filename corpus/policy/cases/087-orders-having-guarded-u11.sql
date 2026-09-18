-- §3.6: the guard is a projection immediately above the Aggregate, so a HAVING above it tests
-- the guarded value and a suppressed group drops out on UNKNOWN. See README §5 uncertainty U7.
SELECT org_id, SUM(amount) AS s
FROM orders GROUP BY org_id HAVING SUM(amount) < 2000 ORDER BY org_id
