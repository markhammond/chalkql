-- expect: not(Filter)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_ts_symbol)
-- expect: ranges=1
SELECT * FROM bars
WHERE ts BETWEEN TIMESTAMP '2026-01-03 00:00:00' AND TIMESTAMP '2026-01-03 23:59:00'
