-- expect: has(Project)
-- expect: has(IndexLookup)
-- expect: has(TopN)
-- expect: not(Sort)
-- expect: not(Fetch)
SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT' ORDER BY volume, ts LIMIT 5
