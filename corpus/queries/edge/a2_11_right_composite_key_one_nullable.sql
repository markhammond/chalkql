-- A2: the same composite key under a RIGHT join.
-- expect: has(Join)
-- expect: join_type=RIGHT
SELECT a.id AS aid, b.id AS bid
FROM (SELECT * FROM sales WHERE id < 3) a RIGHT JOIN sales b
  ON a.region = b.region AND a.amount = b.amount
ORDER BY bid, aid
