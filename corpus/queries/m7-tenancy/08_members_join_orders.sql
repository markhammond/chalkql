-- §8 corpus 08. Two entitled tables in one statement: the entitlements compose across the join, the
-- join key is not entitled, and each table's masks apply per tenancy.
-- expect: principals(all)
SELECT m.id AS mid, m.last_name, o.id AS oid, o.note
FROM members m JOIN orders o ON o.member_id = m.id
ORDER BY m.id, o.id
