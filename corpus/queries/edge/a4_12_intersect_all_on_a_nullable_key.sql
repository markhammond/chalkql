-- A4: INTERSECT ALL over the nullable column, where the duplicated 20 has a multiplicity to take
-- the smaller of.
-- expect: has(SetOp)
SELECT amount FROM sales
INTERSECT ALL
SELECT amount FROM sales WHERE id < 5
ORDER BY 1 NULLS LAST
