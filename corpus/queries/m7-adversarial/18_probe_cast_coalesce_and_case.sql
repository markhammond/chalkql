-- §3.11. Three ways to ask a placeholder what it is standing in for: cast it, replace it, and test
-- it in a CASE. All three see the stand-in, and `COALESCE` proves it is a NULL rather than a value
-- that merely renders as one.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT id,
       CAST(national_id AS VARCHAR) AS cast_id,
       COALESCE(national_id, 'absent') AS filled,
       CASE WHEN first_name = 'T' THEN 1 ELSE 0 END AS hit
FROM members ORDER BY id
