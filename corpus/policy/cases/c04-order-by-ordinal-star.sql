-- CALCITE-5724, CALCITE-5808, CALCITE-2610: ORDER BY by ordinal over an expanded star. Ordinal 3
-- is `first_name`, which this principal sees masked, so the statement sorts the masks; ordinal 1
-- breaks the ties so the case can be compared in order.
SELECT * FROM members ORDER BY 3, 1
