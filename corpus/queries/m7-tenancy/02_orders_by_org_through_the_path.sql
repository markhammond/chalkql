-- §8 corpus 02. A cross-tenancy aggregate reached through the member: SUM(amount) is guarded by
-- the group-size floor for the auditor, and reported Aggregate.
-- expect: principals(all)
-- expect: guarded
SELECT m.org_id, COUNT(*) AS n, SUM(o.amount) AS total
FROM orders o JOIN members m ON m.id = o.member_id
GROUP BY m.org_id
ORDER BY m.org_id
