-- A4: and with an offset as well.
-- expect: has(SetOp)
SELECT id, amount FROM sales
UNION ALL
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS LAST, id
OFFSET 2 ROWS FETCH NEXT 4 ROWS ONLY
