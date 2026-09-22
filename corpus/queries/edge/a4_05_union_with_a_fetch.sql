-- A4: a union with a bound on top of the order.
-- D276: the bound is copied into both branches, so there are three bounded heaps and not one.
-- expect: has(SetOp)
-- expect: count(TopN)=3
SELECT id, amount FROM sales
UNION ALL
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS LAST, id
FETCH NEXT 4 ROWS ONLY
