-- §3.11. `national_id` has no rule that can ever disclose it, so every row carries the placeholder
-- and the output type is widened to nullable. A principal who asks which rows *have* one learns
-- nothing: the answer is no rows, for everybody, because a placeholder is a typed NULL rather than
-- an acknowledgement.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT id FROM members WHERE national_id IS NOT NULL ORDER BY id
