-- expect: has(TopN)
-- expect: not(Sort)
-- expect: not(Fetch)
SELECT symbol, ts FROM bars ORDER BY ts DESC, symbol LIMIT 5 OFFSET 3
