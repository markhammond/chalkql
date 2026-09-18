-- §8 corpus 06. `national_id` has no rule that can ever disclose it: a placeholder per row, the
-- output type widened to nullable, and the column reported Undisclosed.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT id, national_id FROM members ORDER BY id
