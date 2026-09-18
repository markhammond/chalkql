-- §8 corpus 11: a sort on a masked column sorts the masks (§3.1), so the first row is the one
-- whose initial sorts first, not the one whose surname does.
SELECT * FROM members ORDER BY last_name LIMIT 1
