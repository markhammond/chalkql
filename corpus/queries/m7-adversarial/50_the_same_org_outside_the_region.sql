-- D266 §6, class 1. The same organisation's orders *outside* the region the grant confines it to.
-- A confined grant is not an organisation grant with a filter on top: both columns of its tuple must
-- match at once, so asking for the complement of the region returns nothing to the confined
-- principals and everything they were already entitled to to the rest.
-- expect: principals(all)
SELECT id, org_id, region_id FROM orders WHERE org_id = 1 AND region_id <> 2 ORDER BY id
