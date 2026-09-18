-- A2: and as a semi join.
-- expect: has(Read)
SELECT a.id
FROM sales a
WHERE EXISTS (SELECT 1 FROM sales b WHERE a.region = b.region AND a.id < b.id)
ORDER BY a.id
