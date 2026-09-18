-- §3.8, D227. The table joined to itself on the protected column: each occurrence is rewritten
-- separately, so the join compares one leaf's disclosed value with another's. Where both are masked
-- the join is an equality of masks — which is what the auditor's fingerprint is *for* — and where
-- one tenancy discloses and the other does not, the two sides simply do not match.
-- expect: principals(all)
SELECT m.id AS a, m2.id AS b
FROM members m JOIN members m2 ON m.last_name = m2.last_name
WHERE m.id < m2.id
ORDER BY m.id, m2.id
