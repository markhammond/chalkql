-- expect: has(Fetch)
-- expect: not(Sort)
-- expect: not(TopN)
SELECT symbol, ts FROM bars ORDER BY ts, symbol LIMIT ? OFFSET ?
