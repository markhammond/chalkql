-- §3.1: a grouping key on a masked column groups the masks. No k applies: the column is MASKED,
-- not AGGREGATE_ONLY, and its disclosed value is ordinary data.
SELECT first_name, COUNT(*) AS n FROM members GROUP BY first_name ORDER BY first_name
