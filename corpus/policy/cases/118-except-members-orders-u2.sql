-- EXCEPT over the same two branches: the member with no visible orders.
SELECT id FROM members WHERE org_id = 1
EXCEPT
SELECT member_id FROM orders WHERE org_id = 1
ORDER BY id
