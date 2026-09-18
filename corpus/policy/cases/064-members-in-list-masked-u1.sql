-- §3.1, as u1: the same list matches only where the initial mask applied.
SELECT id FROM members WHERE first_name IN ('T', 'R') ORDER BY id
