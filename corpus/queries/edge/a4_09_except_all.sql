-- A4: EXCEPT ALL subtracts multiplicities rather than removing values.
-- expect: has(SetOp)
SELECT region FROM sales
EXCEPT ALL
SELECT region FROM sales WHERE id > 4
ORDER BY 1
