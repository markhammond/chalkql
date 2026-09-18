-- D57 and D58: ARRAY_AGG over a frame produces a LIST per row, in the frame's row order. DuckDB
-- calls it list(). ARRAY_AGG is Calcite's POSTGRESQL library, as in corpus query 08.
-- libraries: POSTGRESQL
-- expect: has(Window)
SELECT symbol, ts,
       ARRAY_AGG(volume) OVER (
         PARTITION BY symbol ORDER BY ts ROWS 3 PRECEDING) AS recent_volumes
FROM bars_small
ORDER BY symbol, ts
