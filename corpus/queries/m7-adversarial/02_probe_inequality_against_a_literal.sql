-- §3.1. The negation of 01, which a differencing attacker prefers: everything that is *not* the
-- guess. It compares the disclosed value too, so the complement is the complement of the mask.
-- expect: principals(all)
SELECT id FROM members WHERE first_name <> 'T' ORDER BY id
