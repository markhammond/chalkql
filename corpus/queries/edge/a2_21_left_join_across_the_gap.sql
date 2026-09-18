-- A2: `sorted` skips 3, so a join on k = k + 1 has a row on one side of the gap and none on the
-- other.
-- expect: has(Join)
-- expect: join_type=LEFT
SELECT s1.k AS lk, s2.k AS rk
FROM sorted s1 LEFT JOIN sorted s2 ON s1.k = s2.k + 1
ORDER BY lk, rk
