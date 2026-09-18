-- A1: DISTINCT collapses the tie before the fetch sees it.
-- expect: has(HashAggregate)
SELECT DISTINCT amount FROM sales ORDER BY amount FETCH NEXT 3 ROWS ONLY
