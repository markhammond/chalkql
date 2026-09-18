-- §36.4. The aggregate form (D261 §2): a test of a permitted shape as the FILTER of a permitted
-- aggregate over a population-only column, guarded by the group-size floor of §3.6 exactly as any
-- population aggregate is. O1 holds two members and O2 three, and the floor is three — so one
-- statement shows the guard withholding a group and disclosing another.
-- expect: principals(all)
-- expect: guarded
-- expect: tested
SELECT org_id, COUNT(*) FILTER (WHERE national_id = ?) AS n
FROM members GROUP BY org_id ORDER BY org_id
