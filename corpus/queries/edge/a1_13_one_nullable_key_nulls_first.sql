-- A1: a single nullable sort key with the NULLs first -- the one shape whose key can itself be
-- null. The boundary key holds one row, so the answer is determined.
-- expect: has(TopN)
SELECT id, amount FROM sales ORDER BY amount NULLS FIRST FETCH NEXT 3 ROWS ONLY
