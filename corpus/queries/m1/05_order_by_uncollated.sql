-- The table's declared collation is (ts, symbol), so this ordering is not the one a plain scan
-- delivers. From D50 the (symbol, ts) index does deliver it, and an unbounded lookup on an ordered
-- index is a scan in that index's key order — cheaper than reading the table and sorting 100 800
-- rows. See ADR 0017; the M1 queries that still sort are 08, 26 and 38.
-- expect: not(Sort)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT * FROM bars ORDER BY symbol, ts
