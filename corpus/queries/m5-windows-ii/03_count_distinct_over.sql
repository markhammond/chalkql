-- D56: the counted multiset, over a frame whose bounds both move.
-- expect: has(Window)
SELECT symbol, ts,
       COUNT(DISTINCT trade_count) OVER (
         PARTITION BY symbol ORDER BY ts ROWS 99 PRECEDING) AS distinct_trades
FROM bars_small
ORDER BY symbol, ts
