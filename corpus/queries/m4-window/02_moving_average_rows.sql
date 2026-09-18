-- expect: has(Window)
-- expect: not(Sort)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_small_symbol_ts)
SELECT symbol, ts, AVG("close") OVER (PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20
FROM bars_small
