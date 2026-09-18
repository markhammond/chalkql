-- A4: EXCEPT ALL over the nullable column.
-- expect: has(SetOp)
SELECT amount FROM sales
EXCEPT ALL
SELECT amount FROM sales WHERE id < 3
ORDER BY 1 NULLS LAST
