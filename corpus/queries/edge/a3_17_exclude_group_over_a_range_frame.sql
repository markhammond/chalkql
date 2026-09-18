-- A3: EXCLUDE GROUP drops the current row and its peers together.
-- expect: has(Window)
SELECT id, COUNT(amount) OVER (
         ORDER BY amount RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE GROUP) AS n
FROM sales ORDER BY id
