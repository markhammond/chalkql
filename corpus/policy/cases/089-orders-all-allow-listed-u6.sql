-- §3.6: every allow-listed aggregate over the column in one statement, all four guarded.
SELECT COUNT(amount) AS c, COUNT(DISTINCT amount) AS cd, SUM(amount) AS s, AVG(amount) AS a
FROM orders
