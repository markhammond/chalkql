-- §8 corpus 02 and 13, as u11: two groups, one of 8 rows and one of 2, so the floor bites once.
SELECT org_id, COUNT(*) AS n, SUM(amount) AS s
FROM orders GROUP BY org_id ORDER BY org_id
