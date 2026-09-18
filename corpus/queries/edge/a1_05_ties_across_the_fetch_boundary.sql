-- A1: `sorted` holds two rows at k = 2, and the fetch boundary falls between them. Either row is
-- a right answer, so the comparison counts the tied group rather than naming its members (D166).
-- compare: top-k-under-ties
-- expect: has(TopN)
SELECT k, v FROM sorted ORDER BY k FETCH NEXT 2 ROWS ONLY
