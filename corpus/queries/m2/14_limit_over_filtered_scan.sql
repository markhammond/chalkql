-- expect: has(Read)
-- expect: has(Filter)
-- expect: has(Fetch)
-- expect: plan_text(goal=[10])
-- expect: rows_scanned_at_most=10
SELECT symbol, ts FROM bars WHERE volume > ? LIMIT 5
