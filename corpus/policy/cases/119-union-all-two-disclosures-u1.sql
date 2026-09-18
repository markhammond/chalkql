-- D202: one output column with two origins that disagree. The meet over the origins is PerRow,
-- and the branch that is masked contributes its masks.
SELECT first_name FROM members WHERE org_id = 1
UNION ALL
SELECT first_name FROM members WHERE org_id = 2
ORDER BY 1
