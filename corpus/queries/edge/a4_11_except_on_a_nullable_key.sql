-- A4: EXCEPT over the same column, so the NULL is removed by another NULL.
-- expect: has(SetOp)
SELECT amount FROM sales
EXCEPT
SELECT amount FROM sales WHERE id > 3
ORDER BY 1 NULLS LAST
