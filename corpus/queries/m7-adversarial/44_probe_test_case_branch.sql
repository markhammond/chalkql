-- D261 class 6. The condition is a permitted shape and the branch is a value: the condition tests,
-- so the leaf computes it, and the branch it selects is the placeholder — one bit, never the value.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id, CASE WHEN national_id = ? THEN national_id END AS branch FROM members ORDER BY id
