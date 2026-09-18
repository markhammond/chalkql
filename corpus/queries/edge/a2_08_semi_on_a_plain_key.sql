-- A2: the same semi join on a plain equality, where the NULL row drops out.
-- expect: has(Read)
SELECT a.id
FROM sales a
WHERE EXISTS (SELECT 1 FROM sales b WHERE a.amount = b.amount)
ORDER BY a.id
