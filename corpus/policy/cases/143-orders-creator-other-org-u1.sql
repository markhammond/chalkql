-- D206: the fail-safe reaches across tenancies. Order 115 is in an org u1 holds no grant in.
SELECT id, org_id, note FROM orders WHERE org_id = 3 ORDER BY id
