-- A4: the same as a DISTINCT union, where two NULLs are equal and collapse (D69).
-- expect: has(SetOp)
SELECT id, amount FROM sales
UNION
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS FIRST, id DESC
