-- expect: has(AsOfJoin)
-- expect: join_type=AsOfJoin:INNER
-- expect: asof_match=LE
SELECT b.symbol, b.ts, f.ts AS funding_ts, f.rate
FROM (SELECT symbol, ts FROM bars WHERE ts > TIMESTAMP '2026-01-14 12:00:00') b
ASOF JOIN funding f MATCH_CONDITION b.ts <= f.ts ON b.symbol = f.symbol
ORDER BY b.ts, b.symbol
