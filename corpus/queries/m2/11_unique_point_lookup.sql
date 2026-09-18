-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT * FROM bars WHERE symbol = ? AND ts = ?
