-- D261 class 6, and the attack §36.3 names: a VALUES list joined on the column would be a thousand
-- probes for the price of one. A join key is not a shape, and the key the join compares is the
-- placeholder — the guesses are the attacker's own and hold no canary.
-- expect: principals(all)
-- expect: policy(u9)
SELECT m.id FROM members m
JOIN (VALUES ('AA-1'), ('AA-2'), ('BB-1')) AS g(guess) ON m.national_id = g.guess
ORDER BY m.id
