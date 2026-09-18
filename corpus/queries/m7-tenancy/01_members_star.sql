-- §8 corpus 01. The star over the entitled table, as every principal. u1 manages O1 and acts as
-- an agent in O2, so one result holds raw names from O1 and initials from O2 — the mixed
-- principal's CASE, per row; u6 audits O1 and sees the fingerprint token; u5 holds no grant and
-- sees nothing.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so reading it as a value is
-- refused for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT * FROM members ORDER BY id
