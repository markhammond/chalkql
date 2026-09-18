-- A4: NULLs at the other end.
-- expect: has(SetOp)
SELECT id, amount FROM sales
UNION ALL
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS LAST, id DESC
