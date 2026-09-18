-- expect: error=VALIDATION
-- expect: position
SELECT b.symbol
FROM bars b ASOF JOIN bars q MATCH_CONDITION b.ts <> q.ts ON b.symbol = q.symbol
