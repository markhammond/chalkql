-- A3: PARTITION BY the nullable amount -- the NULL is a partition of its own, and the two rows at
-- 20 share one.
-- expect: has(Window)
SELECT id, COUNT(*) OVER (PARTITION BY amount) AS n FROM sales ORDER BY id
