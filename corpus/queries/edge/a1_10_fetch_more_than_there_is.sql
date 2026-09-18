-- A1: a fetch larger than the table.
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k FETCH NEXT 100 ROWS ONLY
