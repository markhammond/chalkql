-- §8 corpus 12, §3.4: the argument must be a bare reference. A product with a constant is
-- registered as future work F30, not permitted today.
SELECT SUM(amount * 2) AS s FROM orders
