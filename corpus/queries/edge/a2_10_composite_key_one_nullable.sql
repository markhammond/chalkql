-- A2: two key fields, one of them nullable -- the case a null-aware key accessor nulls whole and a
-- plain one leaves as a tuple holding a null that matches another one.
-- expect: has(Join)
-- expect: join_type=INNER
SELECT a.id AS aid, b.id AS bid
FROM sales a JOIN sales b ON a.region = b.region AND a.amount = b.amount
ORDER BY aid, bid
