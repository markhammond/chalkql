-- D261 §2: the aggregate form — a permitted aggregate's FILTER over a population-only column,
-- guarded by the floor of three. Organization 1 holds five visible members and clears it.
SELECT COUNT(*) FILTER (WHERE national_id = 'NID-1002') AS n FROM members
