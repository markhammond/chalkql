-- A4: one column, ordered by itself, which is the shape whose row is a scalar rather than a tuple.
-- expect: has(SetOp)
SELECT label FROM sales
UNION
SELECT label FROM sales WHERE id < 4
ORDER BY 1
