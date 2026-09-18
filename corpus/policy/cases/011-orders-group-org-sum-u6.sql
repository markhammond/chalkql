-- §8 corpus 02, as u6: `SUM(amount)` is an allow-listed population aggregate, guarded by k = 5.
SELECT org_id, COUNT(*) AS n, SUM(amount) AS s
FROM orders GROUP BY org_id ORDER BY org_id
