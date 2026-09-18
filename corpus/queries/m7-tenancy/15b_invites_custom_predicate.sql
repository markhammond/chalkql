-- §8 corpus 15b. The Custom-mode table, whose predicate is a host-computed list and nothing else.
-- Only the principals that bind `allowed_orgs` see anything.
-- expect: principals(all)
SELECT * FROM invites ORDER BY id
