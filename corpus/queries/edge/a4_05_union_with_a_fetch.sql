-- A4: a union with a bound on top of the order.
-- expect: has(SetOp)
SELECT id, amount FROM sales
UNION ALL
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS LAST, id
FETCH NEXT 4 ROWS ONLY
