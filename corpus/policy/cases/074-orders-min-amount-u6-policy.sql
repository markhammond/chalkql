-- D190: MIN is not a population aggregate at all -- it returns one row's value.
SELECT MIN(amount) AS m FROM orders
