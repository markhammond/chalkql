-- expect: has(Project)
-- expect: has_function(SUBTRACT)
-- expect: has_function(DIVIDE)
SELECT symbol, ts, (high - low) / "open" AS range_pct FROM bars WHERE "open" <> 0
