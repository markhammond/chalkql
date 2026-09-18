-- A2: IS NOT DISTINCT FROM makes two NULLs match, which a plain equality never does.
-- expect: has(Join)
SELECT a.id AS aid, b.id AS bid
FROM sales a JOIN sales b ON a.amount IS NOT DISTINCT FROM b.amount
ORDER BY aid, bid
