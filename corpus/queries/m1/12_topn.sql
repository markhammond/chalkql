-- expect: has(TopN)
-- expect: not(Sort)
-- expect: not(Fetch)
SELECT symbol, ts, "close" FROM bars WHERE volume > 5000 ORDER BY "close" DESC LIMIT 10
