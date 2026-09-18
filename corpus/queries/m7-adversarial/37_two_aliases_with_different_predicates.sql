-- §3.1, §3.3. The same table twice with different tenancy predicates, crossed, so that one result
-- holds both occurrences' disclosures side by side. Each leaf's rules are simplified under its own
-- filter, so a mixed principal gets the raw name from the organisation they manage beside the
-- initial from the one they act in — in one row, from one table, under two aliases.
-- expect: principals(all)
SELECT a.first_name AS in_o1, b.first_name AS in_o2
FROM (SELECT first_name FROM members WHERE org_id = 1) a
CROSS JOIN (SELECT first_name FROM members WHERE org_id = 2) b
ORDER BY a.first_name, b.first_name
