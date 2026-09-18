-- A1: the offset skips the unique first row and the fetch then covers the whole tie.
-- compare: top-k-under-ties
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY
