-- §3.1. The extreme of an ordering is a value, and `LIMIT 1` reads it without naming it. A sort on
-- a masked column sorts the masks, so the extreme is the extreme *mask*.
-- expect: principals(all)
SELECT first_name FROM members ORDER BY first_name, id LIMIT 1
