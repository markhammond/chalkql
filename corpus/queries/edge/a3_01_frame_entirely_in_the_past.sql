-- A3: ROWS BETWEEN 3 PRECEDING AND 2 PRECEDING is empty for the first two rows and holds one or
-- two rows after that -- the frame never contains the current row.
-- expect: has(Window)
SELECT id, SUM(amount) OVER (ORDER BY id ROWS BETWEEN 3 PRECEDING AND 2 PRECEDING) AS s FROM sales ORDER BY id
