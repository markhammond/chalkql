-- §3.1. A common table expression is not a boundary the rewrite can be hidden behind: the pass runs
-- on relational algebra, after the validator has inlined the CTE, so what it sees is a scan.
-- expect: principals(all)
WITH visible AS (SELECT id, first_name, org_id FROM members)
SELECT id, first_name FROM visible ORDER BY id
