-- The plain counterpart of 03: COUNT over a frame, where the argument is a DOUBLE far outside the
-- range of a long. How many values a frame holds cannot depend on what they are, but both sliding
-- accumulators switched on the *result's* kind — BIGINT for a COUNT — and so summed the truncated
-- argument into a checked long that nothing ever read, and the statement raised instead of counting
-- (ADR 0044). DuckDB and the reference executor both count.
-- expect: has(Window)
SELECT symbol, ts,
       COUNT("close" * 1e30) OVER (
         PARTITION BY symbol ORDER BY ts ROWS 9 PRECEDING) AS counted
FROM bars_small
ORDER BY symbol, ts
