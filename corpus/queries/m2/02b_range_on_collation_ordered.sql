-- expect: has(IndexLookup)
-- expect: index(ix_bars_ts_symbol)
-- expect: not(Sort)
-- expect: root_collation=[ts asc_nulls_last, symbol asc_nulls_last]
SELECT * FROM bars WHERE ts BETWEEN ? AND ? ORDER BY ts
