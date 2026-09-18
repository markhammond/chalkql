-- D57: LISTAGG over a frame. V22 is false, so a windowed ordered-set aggregate may not carry a
-- WITHIN GROUP clause (ADR 0018, deviation 2) — the frame's own row order is the order, which the
-- window's ORDER BY fixes. DuckDB spells it string_agg with the ordering inside the call.
-- expect: has(Window)
SELECT symbol, ts,
       LISTAGG(CAST(trade_count AS VARCHAR), ',') OVER (
         PARTITION BY symbol ORDER BY ts ROWS 4 PRECEDING) AS recent
FROM bars_small
ORDER BY symbol, ts
