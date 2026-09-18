-- §8 corpus 12. A value use of a population-only column. POLICY for the auditor, who holds it
-- AGGREGATE_ONLY, and ordinary data for everyone whose rules disclose it in full.
-- expect: principals(all)
-- expect: policy(u6)
-- expect: policy(u6-two-orgs)
-- D266: the regional auditor holds the same auditor role, confined to one region, so
-- `amount` is population-only for it too and the statement is refused the same way.
-- expect: policy(u10-in-a-region)
SELECT id, amount FROM orders ORDER BY id
