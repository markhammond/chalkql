-- A3: MIN and MAX over the partition holding the NULL.
-- expect: has(Window)
SELECT id,
       MIN(amount) OVER (PARTITION BY region) AS lo,
       MAX(amount) OVER (PARTITION BY region) AS hi
FROM sales ORDER BY id
