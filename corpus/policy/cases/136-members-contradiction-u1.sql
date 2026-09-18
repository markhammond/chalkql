-- §3.12, part 2b: the statement's own tenancy conjunct is disjoint from the principal's scope,
-- which is a property of the context and the statement and therefore safe to report.
SELECT * FROM members WHERE org_id = 3
