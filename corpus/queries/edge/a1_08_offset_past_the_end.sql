-- A1: an offset past the end returns nothing at all.
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k OFFSET 10 ROWS FETCH NEXT 2 ROWS ONLY
