-- §8 corpus 19 across two tenancies: one key, one token space, so the join pairs a row in O1
-- with a row in O3 that share a first name.
SELECT m.id FROM members m JOIN members m2 ON m.first_name = m2.first_name ORDER BY m.id
