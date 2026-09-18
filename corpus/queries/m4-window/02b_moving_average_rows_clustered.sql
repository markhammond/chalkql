-- D257: query 02 again, over a table whose (symbol, ts) index is clustered and whose copy covers
-- exactly what this statement projects. Same shape, same answer; what changes is the price the
-- planner puts on the leaf — ranges x seek + rows x scan, rather than rows x lookup row — and what
-- the source does to serve it: slices of the copy instead of a gather through the permutation.
-- expect: has(Window)
-- expect: not(Sort)
-- expect: has(IndexLookup)
-- expect: index(ix_bars_clustered_symbol_ts)
-- expect: plan_text(kind=[INDEX_KIND_CLUSTERED])
-- expect: plan_text(covered=[true])
SELECT symbol, ts, AVG("close") OVER (PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) AS sma20
FROM bars_clustered
