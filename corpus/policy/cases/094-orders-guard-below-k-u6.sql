-- §3.6: one row fewer, and every guarded output of the group is NULL.
SELECT COUNT(amount) AS c, SUM(amount) AS s FROM orders WHERE id <= 104
