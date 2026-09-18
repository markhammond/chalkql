-- D57: LISTAGG in the WITHIN GROUP order, over the standard operator table.
-- expect: has(HashAggregate)
SELECT quote, LISTAGG(symbol, ',') WITHIN GROUP (ORDER BY symbol) AS symbols
FROM symbols
GROUP BY quote
ORDER BY quote
