-- §8 corpus 14. The global grant: every row, raw, visibility = ALL — and audited like any other.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT id, first_name, national_id FROM members ORDER BY id
