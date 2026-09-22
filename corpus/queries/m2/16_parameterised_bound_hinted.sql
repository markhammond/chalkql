-- The pair, half two: the same statement, planned with what the caller expects its parameters to
-- be worth. The bound becomes a row goal of one, so the leaf seeks once and stops instead of being
-- costed and read for its whole range. Executed with the very same values as half one, and the
-- rows are identical: a hint chooses a plan and never an answer.
-- expect: has(IndexLookup)
-- expect: has(Fetch)
-- expect: index(ix_bars_symbol_ts)
-- expect: plan_text(goal=[1])
-- expect: rows_scanned_at_most=1
SELECT symbol, ts FROM bars WHERE symbol >= ? ORDER BY symbol, ts LIMIT ?
