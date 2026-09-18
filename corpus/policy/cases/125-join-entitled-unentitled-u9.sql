-- §3.12: every column of an unentitled table is reported Full and the table has no report row;
-- the entitled side keeps its own row predicate.
SELECT o.id, s.label
FROM orders o JOIN symbols s ON o.status = s.code
WHERE o.org_id = 2
ORDER BY o.id
