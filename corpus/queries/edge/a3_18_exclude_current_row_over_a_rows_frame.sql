-- A3: the same exclusion over a ROWS frame whose ordering is total, so the frame is determined by
-- the key alone.
-- expect: has(Window)
SELECT id, COUNT(amount) OVER (
         ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW) AS n
FROM sales ORDER BY id
