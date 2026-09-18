-- D265 clause (h), class 3. A list the attacker wrote, joined on the target's key: the key is the
-- one column of the target the mechanism itself reads, so a statement that supplies its own keys is
-- the sharpest test that the marker restricts rows rather than decorating them.
-- expect: principals(all)
SELECT o.id, v.k FROM orders o JOIN (VALUES (1), (2), (3), (4), (5), (6), (7)) AS v(k) ON v.k = o.id
ORDER BY o.id
