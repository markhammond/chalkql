-- §8 corpus 09: the pass recurses into the `Correlate`, so the lateral's scan is entitled too.
SELECT m.id, c.n
FROM members m
CROSS JOIN LATERAL (SELECT COUNT(*) AS n FROM orders o WHERE o.member_id = m.id) c
ORDER BY m.id
