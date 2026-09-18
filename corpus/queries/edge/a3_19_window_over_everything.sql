-- A3: no PARTITION BY and no ORDER BY -- one frame holding every row.
-- expect: has(Window)
SELECT id, SUM(amount) OVER () AS s FROM sales ORDER BY id
