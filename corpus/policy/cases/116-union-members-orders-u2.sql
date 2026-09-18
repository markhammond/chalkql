-- A set operation over two entitled leaves; the trace maps output i to input i of each branch.
SELECT id FROM members WHERE org_id = 1
UNION
SELECT member_id FROM orders WHERE org_id = 1
ORDER BY id
