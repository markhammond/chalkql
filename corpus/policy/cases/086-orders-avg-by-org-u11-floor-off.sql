-- k = 0 disables the guard (§6), here through an unspecified column floor over a catalog whose
-- `default_min_group_size` is 0.
SELECT org_id, AVG(amount) AS a FROM orders GROUP BY org_id ORDER BY org_id
