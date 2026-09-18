-- CALCITE-7644, CALCITE-5828: a window whose ORDER BY is an expression rather than a column.
-- The alias is `running_total` and not `running`: RUNNING is a reserved word in the SQL Calcite
-- parses (a MATCH_RECOGNIZE keyword), so the earlier spelling was a parse error and the case never
-- reached the exposure it guards.
-- Calcite unparses it as a positional ordinal into the enclosing select list, which is a
-- different sort key and therefore a different running total.
SELECT id,
       SUM(amount) OVER (PARTITION BY org_id
                         ORDER BY CASE WHEN status = 'DONE' THEN 1 ELSE 0 END, id) AS running_total
FROM orders
