-- A3: EXCLUDE TIES drops the current row's peers and keeps the current row.
-- expect: has(Window)
SELECT id, COUNT(amount) OVER (
         ORDER BY amount RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE TIES) AS n
FROM sales ORDER BY id
