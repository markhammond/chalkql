-- D313: the same on an ordered index. The client resolves the pattern's literal start through the
-- escape and sends the half-open range [BTC, BTD), exactly as it does for the two-operand form.
-- expect: has(IndexLookup)
-- expect: index(ix_bars_small_symbol_ts)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT symbol, ts FROM bars_small WHERE symbol LIKE 'BTC%' ESCAPE '\'
