-- §8 corpus 09. A LATERAL, decorrelated into an ordinary join, with the entitlement enforced inside
-- it. LEFT JOIN LATERAL … ON TRUE rather than CROSS JOIN LATERAL: an aggregate on the inner side of
-- a cross lateral is the shape Calcite 1.42 cannot re-type (14-windows-ii.md §8).
-- expect: principals(all)
SELECT m.id, c.n
FROM members m LEFT JOIN LATERAL (SELECT COUNT(*) AS n FROM orders o WHERE o.member_id = m.id) c
ON TRUE
ORDER BY m.id
