-- expect: has(AsOfJoin)
-- expect: join_type=AsOfJoin:LEFT
--
-- The right side excludes its NULL timestamps on purpose. This query is about a NULL on the *left*
-- — a left row whose time is unknown matches nothing — and DuckDB, which is the third oracle here,
-- matches a NULL left time to a NULL right time instead of dropping it (ADR 0016). Leaving the NULL
-- right rows in would make the comparison about DuckDB's rule rather than about this one; queries
-- 04 and 05 keep them, where both engines agree.
SELECT t.symbol, t.ts, f.rate
FROM (SELECT symbol, CASE WHEN volume > 5000 THEN ts END AS ts
      FROM bars
      WHERE ts < TIMESTAMP '2026-01-01 02:00:00') t
LEFT ASOF JOIN (SELECT symbol, ts, rate FROM funding WHERE ts IS NOT NULL) f
  MATCH_CONDITION t.ts >= f.ts ON t.symbol = f.symbol
ORDER BY t.symbol, t.ts
