-- A1: FETCH NEXT 0 ROWS ONLY -- a bound of nothing, which is not the same as no bound.
-- expect: has(TopN)
SELECT id, amount FROM sales ORDER BY id FETCH NEXT 0 ROWS ONLY
