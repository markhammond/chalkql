-- expect: has(IndexLookup)
-- expect: index(ix_bars_ts_symbol)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT * FROM bars WHERE ts BETWEEN ? AND ?
