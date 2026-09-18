-- A3: COUNT(*) counts the frame's rows and COUNT(amount) counts its non-NULLs, in one window.
-- expect: has(Window)
SELECT id,
       COUNT(*) OVER (ORDER BY id) AS n,
       COUNT(amount) OVER (ORDER BY id) AS na
FROM sales ORDER BY id
