-- §3.4: the same CTE whose column reaches the root is a value use, one Project further up.
WITH v AS (SELECT id, amount FROM orders)
SELECT id, amount FROM v ORDER BY id
