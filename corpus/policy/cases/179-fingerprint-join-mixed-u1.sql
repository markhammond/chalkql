-- §8 corpus 19 for a mixed principal: raw values on one side of the tenancy and initials on the
-- other never match each other, which is the disclosed semantics and not a bug.
SELECT m.id FROM members m JOIN members m2 ON m.first_name = m2.first_name ORDER BY m.id
