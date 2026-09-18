-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: not(Sort)
-- expect: root_collation=[symbol asc_nulls_last, ts asc_nulls_last]
SELECT symbol, ts, "close" FROM bars WHERE symbol = ? AND ts >= ? ORDER BY ts
