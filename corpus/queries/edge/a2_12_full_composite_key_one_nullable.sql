-- A2: and under a FULL join.
-- expect: has(Join)
-- expect: join_type=FULL
SELECT a.id AS aid, b.id AS bid
FROM sales a FULL JOIN sales b ON a.region = b.region AND a.amount = b.amount
ORDER BY aid, bid
