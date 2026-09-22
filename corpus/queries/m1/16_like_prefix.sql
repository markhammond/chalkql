-- A bare prefix pattern is a range, not a predicate: the matcher turns `LIKE 'BTC%'` on the leading
-- STRING key of an ordered index into one prefix range, and nothing is left above the lookup to
-- re-check. The LIKE kernel's own coverage is query 36, whose pattern is `'%USDT'` and can only be
-- evaluated per row.
-- expect: has(IndexLookup)
-- expect: index(ix_bars_symbol_ts)
-- expect: not(Filter)
-- expect: not_function(LIKE)
SELECT symbol, ts FROM bars WHERE symbol LIKE 'BTC%'
