-- A3: LEAD and LAG in one query, both stepping off the ends.
-- expect: has(Window)
SELECT id,
       LAG(amount) OVER (ORDER BY id) AS lg,
       LEAD(amount) OVER (ORDER BY id) AS ld
FROM sales ORDER BY id
