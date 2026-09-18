-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: has(Filter)
-- expect: ranges=1
SELECT * FROM bars WHERE symbol = ? AND volume > ?
