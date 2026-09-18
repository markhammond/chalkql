-- A1: the *offset* lands inside the tie -- which of the two rows at k = 2 was skipped is the
-- executor's business, and only the count is determined.
-- compare: top-k-under-ties
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k OFFSET 2 ROWS FETCH NEXT 2 ROWS ONLY
