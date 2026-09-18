-- expect: has(IndexLookup)
-- expect: index(ix_bars_ts_symbol)
-- expect: not(Filter)
-- expect: ranges=1
SELECT COUNT(*) FROM bars WHERE ts >= TIMESTAMP '2026-01-14 00:00:00'
