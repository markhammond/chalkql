-- A2: both sides' unmatched rows, one of them NULL-keyed.
-- expect: has(Join)
-- expect: join_type=FULL
SELECT a.id AS aid, b.id AS bid
FROM (SELECT * FROM sales WHERE id < 3) a FULL JOIN sales b ON a.amount = b.amount
ORDER BY aid, bid
