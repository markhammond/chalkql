-- An aggregate over the rows the conjunction admits: the group a confined reviewer sees is their own
-- warehouse's, and the quantity is the sum over their own supplier's rows in it.
-- expect: principals(all)
SELECT warehouse_code, SUM(quantity) AS total FROM positions GROUP BY warehouse_code ORDER BY warehouse_code
