-- A4: and as a DISTINCT union.
-- expect: has(SetOp)
SELECT id, amount FROM sales
UNION
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS LAST, id DESC
