-- D207 (part 2b): a column of an unentitled table has no sibling; the per-column report already
-- says Full for it.
SELECT s.code, o.id
FROM symbols s JOIN orders o ON o.status = s.code
WHERE o.org_id = 1
ORDER BY o.id
