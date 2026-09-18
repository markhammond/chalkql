-- §3.6: the boundary. The guard is COUNT(c) at or above k, so a group of exactly k is disclosed.
SELECT COUNT(amount) AS c, SUM(amount) AS s FROM orders WHERE id <= 105
