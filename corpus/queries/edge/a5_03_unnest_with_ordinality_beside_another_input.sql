-- A5: WITH ORDINALITY beside another input -- the ordinal restarts for each left row.
-- expect: has(Unnest)
SELECT t1.x, t2.y, t2.o
FROM (VALUES (1), (2)) AS t1(x), UNNEST(ARRAY[3, 4]) WITH ORDINALITY AS t2(y, o)
ORDER BY 1, 3
