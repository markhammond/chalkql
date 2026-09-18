-- D66: LEFT JOIN LATERAL UNNEST … ON TRUE keeps the empty and NULL lists, one NULL row each, and
-- WITH ORDINALITY numbers the elements from one.
-- expect: has(Unnest)
-- expect: not(Correlate)
SELECT s.symbol, t.tag, t.n
FROM symbols s
LEFT JOIN LATERAL UNNEST(s.tags) WITH ORDINALITY AS t(tag, n) ON TRUE
ORDER BY s.symbol, t.n
