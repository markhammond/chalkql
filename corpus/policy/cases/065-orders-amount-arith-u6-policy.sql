-- §8 corpus 12, §3.4: a value use -- an expression over a population-only column, even one whose
-- value is constant.
SELECT amount - amount AS z FROM orders
