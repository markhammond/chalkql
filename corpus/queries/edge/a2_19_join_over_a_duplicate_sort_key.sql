-- A2: `sorted` advertises an order on a key that repeats, which is what keeps a merge join
-- reachable over a key that is not a key.
-- expect: has(Join)
SELECT s1.k AS k, s1.v AS lv, s2.v AS rv
FROM sorted s1 JOIN sorted s2 ON s1.k = s2.k
ORDER BY k, lv, rv
