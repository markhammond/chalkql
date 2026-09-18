-- D261 class 6. `LIKE` is not a test shape, so the support desk's rule reaches it not at all: what
-- the pattern is matched against is the placeholder, exactly as under NONE (§36.2). A shape the
-- policy did not name is not a comparison the leaf computes. The pattern is a constant because M1
-- compiles one per plan (02-ir.md §6), and it is the attacker's own guess, canary-free.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id FROM members WHERE national_id LIKE 'AA%' ORDER BY id
