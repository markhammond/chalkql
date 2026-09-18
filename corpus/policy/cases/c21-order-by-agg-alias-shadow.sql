-- CALCITE-7744, CALCITE-4987: the select list's alias `amount` shadows the base column `amount`,
-- and the ORDER BY names the aggregate over it. The reported failure is a plan with an aggregate
-- call inside a Project, which is a shape RelToIr has no node for.
SELECT MAX(amount) AS amount, org_id
FROM orders GROUP BY org_id ORDER BY MAX(amount)
