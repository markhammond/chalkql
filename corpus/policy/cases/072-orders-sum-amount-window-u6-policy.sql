-- §8 corpus 12, §3.4: a window aggregate, whose partition can be one row.
SELECT id, SUM(amount) OVER () AS s FROM orders
