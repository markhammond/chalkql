-- §5 corpus 05, windowed half. `wsum` declares Remove, so the frame slides rather than recomputing;
-- the reference executor materialises every frame, and the two must agree either way.
-- expect: has(Window)
-- expect: has_user_function(main.wsum)
SELECT symbol, ts,
       wsum("close", volume) OVER (
         PARTITION BY symbol ORDER BY ts ROWS 9 PRECEDING) AS s
FROM bars_small
ORDER BY symbol, ts
