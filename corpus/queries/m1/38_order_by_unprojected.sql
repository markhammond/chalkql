-- expect: has(Project)
-- expect: has(Sort)
-- expect: not(TopN)
-- expect: not(Fetch)
SELECT symbol, ts FROM bars ORDER BY volume DESC
