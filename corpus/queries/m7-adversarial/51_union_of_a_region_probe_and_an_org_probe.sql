-- D266 §6, class 2. A UNION of a region-only probe and an organisation-only probe. Neither half is
-- reachable on its own for a principal whose grant names both — the grant is one tuple and not two
-- memberships — so a set operation cannot recombine the halves into rows the conjunction excludes.
-- expect: principals(all)
SELECT id FROM orders WHERE region_id = 1
UNION
SELECT id FROM orders WHERE org_id = 1
ORDER BY id
