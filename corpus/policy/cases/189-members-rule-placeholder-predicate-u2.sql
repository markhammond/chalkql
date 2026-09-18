-- D224 with §3.1: a rule placeholder is a value in a projection and never a query use. Under leaf
-- sanitisation the predicate is evaluated over the sanitiser, so what an agent compares here is the
-- rule's stand-in and not the name — which matches nothing, where a manager's `first_name = 'Tara'`
-- finds member 1.
SELECT id FROM members WHERE first_name = 'Tara' ORDER BY id
