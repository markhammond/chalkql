-- A floor of 1: the guard is vacuous for a non-empty group, so no guard is emitted and the
-- two-order group keeps its average. See README §5 uncertainty U6.
SELECT org_id, AVG(amount) AS a FROM orders GROUP BY org_id ORDER BY org_id
