-- §3.1, as u1: raw names on one side of the tenancy and initials on the other never match each
-- other, so u1 sees fewer pairs than u2 does over the same rows.
SELECT a.id AS a_id, b.id AS b_id
FROM members a JOIN members b ON a.first_name = b.first_name AND a.id < b.id
ORDER BY a_id, b_id
