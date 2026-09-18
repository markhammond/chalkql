-- expect: has(Project)
-- expect: not(Sort)
-- expect: not(TopN)
-- expect: not(Fetch)
-- expect: read_projection=2
SELECT symbol FROM bars ORDER BY ts
