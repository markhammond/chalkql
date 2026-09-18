-- D57 and D58: ARRAY_AGG produces a LIST, and CARDINALITY and ITEM read it back.
-- libraries: POSTGRESQL
-- expect: has(HashAggregate)
SELECT symbol, CARDINALITY(closes) AS n, closes[1] AS first_close
FROM (SELECT symbol, ARRAY_AGG("close" ORDER BY ts) AS closes
      FROM bars_small
      GROUP BY symbol)
ORDER BY symbol
