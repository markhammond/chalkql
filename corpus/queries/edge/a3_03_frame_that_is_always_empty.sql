-- A3: a frame that ends before it starts is empty for every row, so every aggregate is its empty
-- value -- NULL for SUM, zero for COUNT.
-- expect: has(Window)
SELECT id,
       SUM(amount) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND 3 PRECEDING) AS s,
       COUNT(*) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND 3 PRECEDING) AS n
FROM sales ORDER BY id
