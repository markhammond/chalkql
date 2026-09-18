-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: not(Filter)
-- expect: ranges=2
-- expect: rows_scanned=produced
SELECT * FROM bars WHERE symbol = 'BTCUSDT' OR symbol = 'ETHUSDT'
