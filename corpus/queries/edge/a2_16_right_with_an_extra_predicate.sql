-- A2: and under RIGHT.
-- expect: has(Join)
-- expect: join_type=RIGHT
SELECT a.id AS aid, b.id AS bid
FROM sales a RIGHT JOIN sales b ON a.region = b.region AND a.id < b.id
ORDER BY bid, aid
