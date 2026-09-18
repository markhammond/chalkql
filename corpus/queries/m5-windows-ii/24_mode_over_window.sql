-- D57 over a frame rather than a group: step 19 covered the windowed holistic aggregates by unit
-- tests only (ADR 0018, deviation 8). MODE's ties go to the smallest value, which is Chalk's rule
-- and not DuckDB's, so the oracles for this one are the reference executor and LINQ.
-- expect: has(Window)
SELECT symbol, ts,
       MODE(trade_count) OVER (
         PARTITION BY symbol ORDER BY ts ROWS 9 PRECEDING) AS common_trades
FROM bars_small
ORDER BY symbol, ts
