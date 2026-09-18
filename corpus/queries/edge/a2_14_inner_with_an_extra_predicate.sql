-- A2: an equality and something else besides, which the join tests on the pair it has matched.
-- expect: has(Join)
-- expect: join_type=INNER
SELECT a.id AS aid, b.id AS bid
FROM sales a JOIN sales b ON a.region = b.region AND a.id < b.id
ORDER BY aid, bid
