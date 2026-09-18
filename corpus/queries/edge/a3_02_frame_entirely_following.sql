-- A3: a frame wholly ahead of the current row, empty for the last two.
-- expect: has(Window)
SELECT id, SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING) AS s FROM sales ORDER BY id
