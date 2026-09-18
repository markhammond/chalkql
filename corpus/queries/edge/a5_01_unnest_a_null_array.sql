-- A5: UNNEST of a NULL array produces no rows at all -- not one row holding NULL -- so under a LEFT
-- LATERAL join the left row survives with a NULL beside it. The array is a column's, because a
-- *constant* NULL array cannot be physically planned (V48, ADR 0024).
-- expect: has(Unnest)
SELECT s.symbol, t.tag
FROM symbols s LEFT JOIN LATERAL UNNEST(s.tags) AS t(tag) ON TRUE
WHERE s.tags IS NULL
ORDER BY 1
