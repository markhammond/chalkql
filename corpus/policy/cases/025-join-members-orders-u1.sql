-- §8 corpus 08: entitlements compose across a join whose key is not entitled; both masks apply
-- per tenancy, in one result.
SELECT m.last_name, o.note
FROM members m JOIN orders o ON o.member_id = m.id
