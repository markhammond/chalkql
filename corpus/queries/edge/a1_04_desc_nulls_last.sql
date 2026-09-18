-- A1: DESC on a nullable key, NULLs last.
-- expect: has(Sort)
SELECT id, amount FROM sales ORDER BY amount DESC NULLS LAST, id
