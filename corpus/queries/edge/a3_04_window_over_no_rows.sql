-- A3: a window whose input is empty produces no rows, not one row of nulls.
-- expect: has(Window)
SELECT id, SUM(amount) OVER (PARTITION BY region) AS s FROM sales WHERE id < 0 ORDER BY id
