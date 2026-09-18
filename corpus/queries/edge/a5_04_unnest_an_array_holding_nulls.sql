-- A5: a NULL *element* is a row, where a NULL *array* is none.
-- expect: has(Unnest)
SELECT * FROM UNNEST(ARRAY[1, CAST(NULL AS INTEGER), 3]) AS t(y) ORDER BY 1
