-- A4: UNION ALL ordered by the nullable key with the NULLs first and a second key the other way.
-- expect: has(SetOp)
SELECT id, amount FROM sales
UNION ALL
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS FIRST, id DESC
