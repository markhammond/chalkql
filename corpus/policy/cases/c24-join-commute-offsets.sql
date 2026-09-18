-- CALCITE-4126, CALCITE-5842: a join whose inputs JOIN_COMMUTE and JOIN_ASSOCIATE may swap. The
-- masked column belongs to `members` whichever side the join ended up on, and the disclosure
-- flow concatenates a join's inputs in input order.
SELECT o.id, m.first_name
FROM orders o JOIN members m ON o.member_id = m.id
WHERE m.org_id = 1
