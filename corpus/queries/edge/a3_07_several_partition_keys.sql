-- A3: two partition keys, one of them nullable.
-- expect: has(Window)
SELECT id, COUNT(*) OVER (PARTITION BY region, amount) AS n FROM sales ORDER BY id
