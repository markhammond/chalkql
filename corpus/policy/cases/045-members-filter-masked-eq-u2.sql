-- §3.1: a filter on a masked column compares the mask; the initial, not the name.
SELECT id FROM members WHERE first_name = 'T' ORDER BY id
