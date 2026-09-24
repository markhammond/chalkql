-- A probe through a field: an agent may not read a tier-1 card, and filtering on `tier = 1` must not
-- tell it which profiles hold one. The predicate compares the disclosed composite's field, which is
-- NULL for every card the agent is not given, so the probe finds exactly the cards it may see.
-- expect: principals(all)
SELECT p.id, p.org_id FROM main.profiles p WHERE p.contact.tier = 1 ORDER BY p.id
