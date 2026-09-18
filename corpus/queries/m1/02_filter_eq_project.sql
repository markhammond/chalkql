-- expect: not(Filter)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: read_projection=3
SELECT symbol, ts, "close" FROM bars WHERE symbol = 'BTCUSDT'
