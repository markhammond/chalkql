-- D58 and D66 together: a list built by ARRAY_AGG and flattened again.
-- libraries: POSTGRESQL
-- expect: has(Unnest)
SELECT symbol, e
FROM (SELECT symbol, ARRAY_AGG("close" ORDER BY ts) AS closes FROM bars_small GROUP BY symbol)
CROSS JOIN UNNEST(closes) AS u(e)
ORDER BY symbol, e
