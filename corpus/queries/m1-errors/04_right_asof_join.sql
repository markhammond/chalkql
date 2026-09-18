-- expect: error=PARSE
-- expect: position
SELECT b.symbol, b.ts
FROM bars b RIGHT ASOF JOIN bars q MATCH_CONDITION b.ts >= q.ts ON b.symbol = q.symbol
