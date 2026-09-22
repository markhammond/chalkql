-- D282: LIKE 'p%' on the leading STRING key of an ordered index. The matcher turns it into one
-- prefix range, and the client resolves that into the plain half-open range [BTC, BTD) before the
-- source sees it — so an ordinary ordered string index serves a LIKE with no change at all, and
-- nothing is left above the lookup to re-check.
-- expect: has(IndexLookup)
-- expect: index(ix_bars_small_symbol_ts)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT symbol, ts FROM bars_small WHERE symbol LIKE 'BTC%'
