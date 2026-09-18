-- A1: the same with the NULLs last, where the boundary key is the duplicated 20 -- so which of
-- the two rows at 20 comes back is the executor's business.
-- compare: top-k-under-ties
-- expect: has(TopN)
SELECT id, amount FROM sales ORDER BY amount NULLS LAST FETCH NEXT 3 ROWS ONLY
