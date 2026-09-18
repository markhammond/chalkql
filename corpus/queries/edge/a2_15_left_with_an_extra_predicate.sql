-- A2: the same under LEFT, where a row that matched the equality but failed the extra predicate
-- is still an unmatched row.
-- expect: has(Join)
-- expect: join_type=LEFT
SELECT a.id AS aid, b.id AS bid
FROM sales a LEFT JOIN sales b ON a.region = b.region AND a.id < b.id
ORDER BY aid, bid
