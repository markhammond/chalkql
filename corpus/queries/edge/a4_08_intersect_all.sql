-- A4: INTERSECT ALL keeps the smaller multiplicity of each value.
-- expect: has(SetOp)
SELECT region FROM sales
INTERSECT ALL
SELECT region FROM sales WHERE id < 5
ORDER BY 1
