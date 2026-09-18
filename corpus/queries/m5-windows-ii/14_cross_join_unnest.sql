-- D66: CROSS JOIN UNNEST. The empty and NULL tag lists contribute no rows.
-- expect: has(Unnest)
-- expect: not(Correlate)
SELECT s.symbol, t.tag
FROM symbols s CROSS JOIN UNNEST(s.tags) AS t(tag)
ORDER BY s.symbol, t.tag
