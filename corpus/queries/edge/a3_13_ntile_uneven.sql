-- A3: four buckets over six rows, which do not divide -- the earlier buckets take the extra rows.
-- expect: has(Window)
SELECT id, NTILE(4) OVER (ORDER BY id) AS t FROM sales ORDER BY id
