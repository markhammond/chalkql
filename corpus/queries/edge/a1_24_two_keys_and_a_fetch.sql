-- A1: two keys and a fetch, the tie broken before the boundary.
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k, v FETCH NEXT 3 ROWS ONLY
