-- §8 corpus 09, as u2: member 10 has no visible orders and still appears, with a count of zero.
SELECT m.id, c.n
FROM members m
CROSS JOIN LATERAL (SELECT COUNT(*) AS n FROM orders o WHERE o.member_id = m.id) c
ORDER BY m.id
