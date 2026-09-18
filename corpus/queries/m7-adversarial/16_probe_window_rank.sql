-- §3.1. A window orders every row against every other without an aggregate anywhere, and its
-- partition can be one row. The order key is the disclosed value, so the rank is a rank of masks.
-- expect: principals(all)
SELECT id, RANK() OVER (ORDER BY last_name) AS r FROM members ORDER BY id
