-- §3.1: one table, two occurrences, judged separately. The outer occurrence never touches
-- `amount`; the inner one aggregates it.
SELECT o.id, t.total
FROM orders o CROSS JOIN (SELECT SUM(amount) AS total FROM orders) t
WHERE o.org_id = 1
ORDER BY o.id
