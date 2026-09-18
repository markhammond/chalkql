-- A3: NTH_VALUE over a partition of three, so the second exists and the frame before it does not.
-- expect: has(Window)
SELECT id, NTH_VALUE(amount, 2) OVER (PARTITION BY region ORDER BY id) AS v FROM sales ORDER BY id
