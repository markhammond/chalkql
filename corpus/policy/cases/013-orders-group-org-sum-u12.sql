-- §8 corpus 02, as the mixed principal u12: an allow-listed aggregate is the one permitted
-- consumer, so the statement stands and every group is guarded -- the manager's org too.
SELECT org_id, COUNT(*) AS n, SUM(amount) AS s
FROM orders GROUP BY org_id ORDER BY org_id
