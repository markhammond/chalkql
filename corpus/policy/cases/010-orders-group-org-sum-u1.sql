-- §8 corpus 02, as u1: a cross-tenancy aggregate; `amount` is FULL, so no guard is emitted.
SELECT org_id, COUNT(*) AS n, SUM(amount) AS s
FROM orders GROUP BY org_id ORDER BY org_id
