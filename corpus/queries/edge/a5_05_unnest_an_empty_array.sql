-- A5: an empty array produces no rows either, for a different reason from a NULL one -- and under a
-- LEFT LATERAL join the two are indistinguishable from outside. The array is a column's: Calcite's
-- ARRAY[] needs at least one element, so an empty constant array cannot be written (V49, ADR 0024).
-- expect: has(Unnest)
SELECT s.symbol, t.tag
FROM symbols s LEFT JOIN LATERAL UNNEST(s.tags) AS t(tag) ON TRUE
WHERE CARDINALITY(s.tags) = 0
ORDER BY 1
