-- INTERSECT over the same two branches.
SELECT id FROM members WHERE org_id = 1
INTERSECT
SELECT member_id FROM orders WHERE org_id = 1
ORDER BY id
