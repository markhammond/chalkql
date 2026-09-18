-- expect: has(Filter)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: ranges=2
-- expect: has_function(IS_NOT_NULL)
SELECT * FROM bars WHERE symbol IN ('BTCUSDT', 'ETHUSDT') AND trade_count IS NOT NULL
