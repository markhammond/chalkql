-- §8 corpus 12, §3.4: an aggregate's FILTER is a query use. Two such sums differing by one row
-- reveal that row, guard or no guard.
SELECT SUM(amount) FILTER (WHERE amount > 0) AS s FROM orders
