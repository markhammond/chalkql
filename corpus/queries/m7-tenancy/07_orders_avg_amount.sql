-- §8 corpus 07. The allow-listed aggregate over a population-only column: permitted, guarded, and
-- reported Aggregate. Its twin — a value use of the same column — is corpus 12.
-- expect: principals(all)
-- expect: guarded
SELECT AVG(amount) AS a FROM orders
