-- A2: and the FULL join over the same offset key, so both sides' orphans appear.
-- expect: has(Join)
-- expect: join_type=FULL
SELECT s1.k AS lk, s2.k AS rk
FROM sorted s1 FULL JOIN sorted s2 ON s1.k = s2.k + 2
ORDER BY lk, rk
