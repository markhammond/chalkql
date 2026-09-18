-- CALCITE-6504: a correlated sub-query inside an equi-join's ON clause, which is the exact shape
-- JOIN_SUB_QUERY_TO_CORRELATE is reported to rewrite into an incorrect tree.
SELECT m.id, o.id
FROM members m
JOIN orders o
  ON o.member_id = (SELECT MIN(m2.id) FROM members m2 WHERE m2.org_id = m.org_id)
