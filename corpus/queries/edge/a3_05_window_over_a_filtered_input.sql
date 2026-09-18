-- A3: the window sees the filtered rows and only those.
-- expect: has(Window)
-- expect: has(Filter)
SELECT id, SUM(amount) OVER (ORDER BY id) AS s FROM sales WHERE region = 'EAST' ORDER BY id
