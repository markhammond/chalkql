-- §3.4, §3.6: an aggregate argument on a population-only column, bare, allow-listed, guarded.
SELECT SUM(amount) AS s FROM orders
