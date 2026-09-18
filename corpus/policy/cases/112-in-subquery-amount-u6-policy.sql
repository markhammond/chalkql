-- The same statement where the column is population-only: a query use inside a sub-query is a
-- query use, and the trace follows it across the sub-query boundary (§3.4).
SELECT id FROM members WHERE id IN (SELECT member_id FROM orders WHERE amount > 100)
ORDER BY id
