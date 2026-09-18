-- The same statement for a principal with no member rows at all: the outer scan is empty, so the
-- sub-query is never asked.
SELECT id FROM members m WHERE EXISTS (SELECT 1 FROM orders o WHERE o.member_id = m.id)
ORDER BY id
