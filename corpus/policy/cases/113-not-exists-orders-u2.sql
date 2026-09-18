-- NOT EXISTS over the entitled inner table: the member with no visible order is the answer, and
-- "no visible order" is what the principal is entitled to know.
SELECT id FROM members m WHERE NOT EXISTS (SELECT 1 FROM orders o WHERE o.member_id = m.id)
ORDER BY id
