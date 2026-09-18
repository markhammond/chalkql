-- expect: has(AsOfJoin)
-- expect: join_type=AsOfJoin:LEFT
-- expect: asof_match=GE
SELECT b.symbol, b.ts, f.rate
FROM (SELECT symbol, ts FROM bars WHERE ts < TIMESTAMP '2026-01-01 06:00:00') b
LEFT ASOF JOIN funding f MATCH_CONDITION b.ts >= f.ts ON b.symbol = f.symbol
ORDER BY b.ts, b.symbol
