-- A1: three keys, two directions and an explicit null placement, with an offset and a fetch.
-- expect: has(TopN)
SELECT id, region, amount FROM sales ORDER BY region DESC, amount NULLS LAST, id OFFSET 1 ROWS FETCH NEXT 4 ROWS ONLY
