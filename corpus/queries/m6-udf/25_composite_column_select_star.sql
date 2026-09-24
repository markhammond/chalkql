-- `SELECT *` over a table with composite columns carries each whole, in the table's column order.
-- expect: count(FieldAccess)=0
SELECT *
FROM quotes
ORDER BY id
