-- A4: the same as a DISTINCT union, so the duplicated key collapses.
-- expect: has(SetOp)
SELECT k FROM sorted
UNION
SELECT k FROM sorted WHERE k < 4
ORDER BY 1
