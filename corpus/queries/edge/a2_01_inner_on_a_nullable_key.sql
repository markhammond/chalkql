-- A2: an equality on a nullable key -- a NULL matches nothing, not even another NULL.
-- expect: has(Join)
-- expect: join_type=INNER
SELECT a.id AS aid, b.id AS bid
FROM sales a JOIN sales b ON a.amount = b.amount
ORDER BY aid, bid
