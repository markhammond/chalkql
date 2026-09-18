-- G: IS DISTINCT FROM in a projection rather than as a join key or a filter, which is its own code
-- path. Never NULL, whatever its arguments are.
-- expect: has(Project)
SELECT id,
       amount IS DISTINCT FROM CAST(NULL AS INTEGER) AS d_null,
       amount IS NOT DISTINCT FROM CAST(NULL AS INTEGER) AS nd_null,
       amount IS DISTINCT FROM 20 AS d_twenty,
       amount IS NOT DISTINCT FROM 20 AS nd_twenty
FROM sales ORDER BY id
