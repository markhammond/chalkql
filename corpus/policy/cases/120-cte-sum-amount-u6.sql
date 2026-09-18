-- §3.4: the trace follows bare pass-throughs through the CTE's projection, so the only consumer
-- of `amount` is the outer SUM and the statement stands.
WITH v AS (SELECT org_id, amount FROM orders)
SELECT org_id, SUM(amount) AS s FROM v GROUP BY org_id ORDER BY org_id
