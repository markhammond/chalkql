-- A2: a NULL key on the side the hash join builds from, under an operator that has to answer for
-- the rows that matched nothing.
-- expect: has(Join)
-- expect: join_type=RIGHT
SELECT a.id AS aid, b.id AS bid
FROM (SELECT * FROM sales WHERE id < 3) a RIGHT JOIN sales b ON a.amount = b.amount
ORDER BY bid, aid
