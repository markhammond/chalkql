-- D261 §2: the aggregate form — a permitted aggregate's FILTER over a population-only column,
-- guarded by the floor of three over the rows the test selects (D364). One identifier selects one
-- of organization 1's five visible members, below the floor, so the count is withheld.
SELECT COUNT(*) FILTER (WHERE national_id = 'NID-1002') AS n FROM members
