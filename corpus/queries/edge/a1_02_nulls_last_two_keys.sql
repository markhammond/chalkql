-- A1: the same order with the NULLs at the other end.
-- expect: has(Sort)
SELECT id, amount FROM sales ORDER BY amount NULLS LAST, id
