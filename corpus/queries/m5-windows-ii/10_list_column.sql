-- D58: a LIST column straight out of a POCO member, with the NULL and empty cases in the data.
-- expect: has(Read)
SELECT symbol, CARDINALITY(tags) AS n, tags[1] AS first_tag
FROM symbols
ORDER BY symbol
