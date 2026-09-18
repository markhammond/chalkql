-- D261 class 6. `IS NULL` is not a test shape (§36.3): a host that must hide nullness does not grant
-- a test on a nullable column, and asking after nullness reads the placeholder, which is NULL for
-- every row under PlaceholdersAsNull.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id FROM members WHERE national_id IS NULL ORDER BY id
