-- §8 corpus 12, §3.4: a join condition is a query use, on both occurrences of the table.
SELECT a.id FROM orders a JOIN orders b ON a.amount = b.amount
