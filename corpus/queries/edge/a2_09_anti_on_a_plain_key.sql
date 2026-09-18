-- A2: and its anti join, which keeps exactly the NULL row.
-- expect: has(Read)
SELECT a.id
FROM sales a
WHERE NOT EXISTS (SELECT 1 FROM sales b WHERE a.amount = b.amount)
ORDER BY a.id
