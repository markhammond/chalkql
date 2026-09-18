-- A1: descending, with an offset and a fetch.
-- expect: has(TopN)
SELECT id FROM sales ORDER BY id DESC OFFSET 1 ROWS FETCH NEXT 3 ROWS ONLY
