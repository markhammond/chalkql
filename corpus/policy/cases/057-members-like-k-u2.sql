-- §3.1: LIKE over the initial mask. The design's own example of what the query-field rule used
-- to break: the row takes part in the statement with its masked value.
SELECT id FROM members WHERE last_name LIKE 'K%' ORDER BY id
