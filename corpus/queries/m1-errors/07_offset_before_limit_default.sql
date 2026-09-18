-- expect: error=PARSE
-- expect: position
SELECT symbol, ts FROM bars ORDER BY ts, symbol OFFSET 3 LIMIT 5
