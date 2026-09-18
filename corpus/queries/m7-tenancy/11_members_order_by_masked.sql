-- §8 corpus 11. A sort on a masked column sorts the masks: an agent's first row is the first by
-- *initial*, which is what makes leaf sanitisation different from the query-field rule it replaced.
-- expect: principals(all)
SELECT id, last_name FROM members ORDER BY last_name, id LIMIT 1
