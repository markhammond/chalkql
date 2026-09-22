-- expect: has(TopN)
-- expect: not(Sort)
-- expect: not(Fetch)
SELECT symbol, ts FROM bars ORDER BY volume DESC, symbol, ts LIMIT ?
