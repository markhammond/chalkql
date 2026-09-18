-- expect: has(HashAggregate)
-- expect: has(Project)
-- expect: has(Sort)
-- expect: measures=[SUM0, COUNT]
SELECT symbol, AVG("close") AS avg_close FROM bars GROUP BY symbol ORDER BY symbol
