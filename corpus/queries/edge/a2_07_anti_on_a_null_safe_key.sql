-- A2: the anti join of the same, which must therefore be empty.
-- expect: has(Read)
SELECT a.id
FROM sales a
WHERE NOT EXISTS (SELECT 1 FROM sales b WHERE a.amount IS NOT DISTINCT FROM b.amount)
ORDER BY a.id
