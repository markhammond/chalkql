-- A4: three inputs, over the ordered table's duplicated key.
-- expect: has(SetOp)
SELECT k FROM sorted
UNION ALL
SELECT k FROM sorted WHERE k < 3
UNION ALL
SELECT k FROM sorted WHERE k > 2
ORDER BY 1
