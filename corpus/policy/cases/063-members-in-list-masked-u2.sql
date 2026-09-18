-- §3.1: an IN list over a masked column tests the masks.
SELECT id FROM members WHERE first_name IN ('T', 'R') ORDER BY id
