-- §3.1 and §3.4: the same shape where the outer occurrence projects `amount`. One bad
-- occurrence refuses the statement; the aggregate on the other is no defence.
SELECT o.amount, t.total
FROM orders o CROSS JOIN (SELECT SUM(amount) AS total FROM orders) t
WHERE o.org_id = 1
ORDER BY o.id
