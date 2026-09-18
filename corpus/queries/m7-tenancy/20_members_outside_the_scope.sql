-- §8 corpus 20. A statement whose own tenancy conjunct is disjoint from the principal's scope:
-- zero rows, visibility = SOME, and no error. `contradiction` — naming the column and saying the
-- requested tenancy is outside the scope — is part 2b's.
-- expect: principals(all)
-- D261: `national_id` is population-only for the counting role, so a star over it is refused
-- for that principal exactly as any population-only column is (§3.4).
-- expect: policy(u9)
SELECT * FROM members WHERE org_id = 3 ORDER BY id
