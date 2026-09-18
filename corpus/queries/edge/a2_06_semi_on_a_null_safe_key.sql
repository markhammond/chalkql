-- A2: a semi join whose key is null-safe, so the NULL row qualifies through itself.
-- expect: has(Read)
SELECT a.id
FROM sales a
WHERE EXISTS (SELECT 1 FROM sales b WHERE a.amount IS NOT DISTINCT FROM b.amount)
ORDER BY a.id
