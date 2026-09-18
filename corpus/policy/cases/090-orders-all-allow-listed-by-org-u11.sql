-- §3.6: the same four in a grouping with one group below the floor -- every guarded output of
-- that group is NULL, the counts included.
SELECT org_id, COUNT(amount) AS c, COUNT(DISTINCT amount) AS cd, SUM(amount) AS s,
       AVG(amount) AS a
FROM orders GROUP BY org_id ORDER BY org_id
