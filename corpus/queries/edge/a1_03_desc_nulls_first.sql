-- A1: DESC on a nullable key, NULLs first.
-- expect: has(Sort)
SELECT id, amount FROM sales ORDER BY amount DESC NULLS FIRST, id
