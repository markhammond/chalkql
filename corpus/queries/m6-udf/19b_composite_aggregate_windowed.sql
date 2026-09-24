-- The same aggregate over a frame (it is declared WINDOW): each frame's composite is written into the
-- window's measure column, and the query above takes it apart. `close_range` declares no Remove, so
-- every frame is recomputed — the answer the reference executor gives by materialising it.
-- expect: has(Window)
-- expect: has_user_function(main.close_range)
-- expect: count(FieldAccess)=2
SELECT s.symbol, s.ts, (r).low AS low, (r).high AS high
FROM (
  SELECT symbol, ts,
         close_range("close") OVER (PARTITION BY symbol ORDER BY ts ROWS 9 PRECEDING) AS r
  FROM bars_small) AS s
ORDER BY s.symbol, s.ts
