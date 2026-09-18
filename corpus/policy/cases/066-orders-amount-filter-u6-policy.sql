-- §8 corpus 12, §3.4: a query use -- a predicate on a raw value is an oracle, and `<` and `>`
-- extract a value by binary search.
SELECT id FROM orders WHERE amount > 0 ORDER BY id
