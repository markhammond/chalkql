-- A1: the fetch takes both rows of the tie, so nothing is ambiguous.
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k FETCH NEXT 3 ROWS ONLY
