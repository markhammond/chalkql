-- A2: the same, with a non-equi predicate that only one of the tied pairs satisfies.
-- expect: has(Join)
SELECT s1.k AS k, s1.v AS lv, s2.v AS rv
FROM sorted s1 JOIN sorted s2 ON s1.k = s2.k AND s1.v < s2.v
ORDER BY k, lv, rv
