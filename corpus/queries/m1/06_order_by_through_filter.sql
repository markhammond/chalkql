-- expect: not(Sort)
-- expect: not(Filter)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
SELECT symbol, ts, "close" FROM bars WHERE symbol = 'ETHUSDT' ORDER BY ts, symbol
