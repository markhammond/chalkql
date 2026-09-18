-- A2: the left row whose amount is NULL matches nothing and is padded.
-- expect: has(Join)
-- expect: join_type=LEFT
SELECT a.id AS aid, b.id AS bid
FROM sales a LEFT JOIN sales b ON a.amount = b.amount
ORDER BY aid, bid
