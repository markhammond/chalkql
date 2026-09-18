-- §3.6 inside a Correlate: the guard applies to the aggregate the lateral computes, so a
-- per-member sum is suppressed for every member -- no member has five visible orders.
SELECT m.id, c.s
FROM members m
CROSS JOIN LATERAL (SELECT SUM(o.amount) AS s FROM orders o WHERE o.member_id = m.id) c
ORDER BY m.id
