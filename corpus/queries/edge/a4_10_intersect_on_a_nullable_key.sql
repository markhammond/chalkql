-- A4: INTERSECT over the nullable column -- two NULLs are equal in a set operation (D69).
-- expect: has(SetOp)
SELECT amount FROM sales
INTERSECT
SELECT amount FROM sales WHERE id > 3
ORDER BY 1 NULLS FIRST
