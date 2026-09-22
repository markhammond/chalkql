-- The pair, half one: the same statement with no hints at all. Both bounds are parameters, so the
-- planner cannot see how many rows anything above this leaf wants, and the leaf is planned as
-- though every row of its range were wanted.
-- expect: has(IndexLookup)
-- expect: has(Fetch)
-- expect: index(ix_bars_symbol_ts)
-- expect: not_plan_text(goal=)
SELECT symbol, ts FROM bars WHERE symbol >= ? ORDER BY symbol, ts LIMIT ?
