-- A5: UNNEST of a constant array beside another input, which is a cross join of the two.
-- expect: has(Unnest)
SELECT t1.x, t2.y
FROM (VALUES (1), (2)) AS t1(x), UNNEST(ARRAY[3, 4]) AS t2(y)
ORDER BY 1, 2
