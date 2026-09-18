-- A1: descending, with the tie split by the boundary again.
-- compare: top-k-under-ties
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k DESC FETCH NEXT 2 ROWS ONLY
