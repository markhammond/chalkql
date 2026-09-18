-- A3: LAG with both an offset and a default, so the window carries two constants past its input.
-- expect: has(Window)
SELECT id, LAG(amount, 2, -1) OVER (ORDER BY id) AS l FROM sales ORDER BY id
