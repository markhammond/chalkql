-- §3.12: one report row per entitled table the plan touches, each with its own descriptor hash
-- and its own visibility.
SELECT m.id, o.id AS order_id
FROM members m JOIN orders o ON o.member_id = m.id
ORDER BY m.id, order_id
