-- A3: NTILE over six rows and two buckets.
-- expect: has(Window)
SELECT id, NTILE(2) OVER (ORDER BY id) AS t FROM sales ORDER BY id
