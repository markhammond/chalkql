-- A1: a fetch above a filter, over the partition holding the NULL.
-- expect: has(TopN)
-- expect: has(Filter)
SELECT id, amount FROM sales WHERE region = 'WEST' ORDER BY amount NULLS LAST FETCH NEXT 2 ROWS ONLY
