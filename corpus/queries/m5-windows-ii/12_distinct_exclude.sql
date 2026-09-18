-- D56 with an EXCLUDE: the multiset is rebuilt per row (ADR 0018).
-- expect: has(Window)
SELECT symbol, ts,
       COUNT(DISTINCT trade_count) OVER (
         PARTITION BY symbol ORDER BY ts ROWS 9 PRECEDING EXCLUDE CURRENT ROW) AS neighbours
FROM bars_small
ORDER BY symbol, ts
