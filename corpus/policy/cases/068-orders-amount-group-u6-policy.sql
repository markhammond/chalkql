-- §8 corpus 12, §3.4: a grouping key is a query use -- without `statistical`, a raw group key
-- names its own rows.
SELECT amount, COUNT(*) AS n FROM orders GROUP BY amount
