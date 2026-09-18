-- A4: a union of two inputs that both advertise an order on a key that repeats.
-- expect: has(SetOp)
SELECT k FROM sorted
UNION ALL
SELECT k FROM sorted WHERE k < 4
ORDER BY 1
