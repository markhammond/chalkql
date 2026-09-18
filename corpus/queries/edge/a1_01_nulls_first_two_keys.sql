-- A1: NULLS FIRST on the nullable key, with a second key to make the order total.
-- expect: has(Sort)
SELECT id, amount FROM sales ORDER BY amount NULLS FIRST, id
