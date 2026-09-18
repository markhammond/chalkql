-- §5 corpus 02. A SQL-bodied aggregate is expanded into the built-in aggregates it is written over
-- plus a projection, so the plan has two SUMs and no wavg.
-- expect: has(HashAggregate)
-- expect: not_user_function
SELECT symbol, wavg("close", volume) AS w
FROM bars_small
GROUP BY symbol
ORDER BY symbol
