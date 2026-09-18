-- §6 corpus 05. An ASOF join whose right side is in another source. The step 17 operator is
-- unchanged behind the boundary: the same answer as the all-local ASOF, which is what the
-- differential asserts.
-- expect: has(AsOfJoin)
-- expect: join_type=AsOfJoin:LEFT
-- expect: has(RemoteQuery)
SELECT b.symbol, b.ts, f.rate
FROM (SELECT symbol, ts FROM bars WHERE ts < TIMESTAMP '2026-01-01 02:00:00') b
LEFT ASOF JOIN duck.funding f MATCH_CONDITION b.ts >= f.ts ON b.symbol = f.symbol
ORDER BY b.ts, b.symbol
