-- §8 corpus 13. O2 has two orders and the floor is three, so that group's AVG is NULL for the
-- auditor — a withheld value rather than "no rows", which is why it is reported Aggregate.
-- expect: principals(all)
-- expect: guarded
SELECT org_id, AVG(amount) AS a FROM orders GROUP BY org_id ORDER BY org_id
