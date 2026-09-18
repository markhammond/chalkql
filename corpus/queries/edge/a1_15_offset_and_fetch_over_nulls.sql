-- A1: an offset and a fetch over the nullable key, both boundaries determined.
-- expect: has(TopN)
SELECT id, amount FROM sales ORDER BY amount OFFSET 1 ROWS FETCH NEXT 3 ROWS ONLY
