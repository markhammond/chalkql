-- §8 corpus 05. A predicate on a withheld column compares the placeholder, never the raw value, so
-- this returns every visible row rather than none.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT id FROM members WHERE national_id IS NULL ORDER BY id
