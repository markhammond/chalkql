-- A2: a semi join on the composite key.
-- expect: has(Read)
SELECT a.id
FROM sales a
WHERE EXISTS (
  SELECT 1 FROM sales b WHERE b.id < 4 AND a.region = b.region AND a.amount = b.amount)
ORDER BY a.id
