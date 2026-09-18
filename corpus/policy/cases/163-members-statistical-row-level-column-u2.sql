-- D203 (part 2b): one row-level column beside a raw key is a per-row membership test with no k
-- at all, so the statement is refused.
SELECT id, first_name, COUNT(*) AS n FROM members GROUP BY id, first_name
