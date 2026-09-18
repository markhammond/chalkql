-- D206 under ByRules: the same row, and no disclosure of its note -- the ungranted tenancy's
-- rules are what apply, and none of them match.
SELECT id, org_id, note FROM orders WHERE org_id = 3 ORDER BY id
