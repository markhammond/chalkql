-- D266 corpus 37. The join of `orders` to `members` under a confined grant. A member's row carries
-- no region at all, so the confining kind does not resolve there and the grant reaches no member:
-- u10 and u11 see their orders and lose every row of the join, while u1 and u2 — whose grants confine
-- nothing — are unchanged. That is §3's rule seen from a statement rather than from a descriptor.
-- expect: principals(all)
SELECT o.id AS oid, o.region_id, m.id AS mid, m.last_name
FROM orders o JOIN members m ON m.id = o.member_id
ORDER BY o.id, m.id
