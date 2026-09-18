-- D261 class 6. `IN (SELECT …)` is not the `IN (list)` shape: the other operand is a relation the
-- statement can vary per row, so it is not a value the policy admits beside the column, and what the
-- sub-query is compared with is the placeholder.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id FROM members WHERE national_id IN (SELECT national_id FROM members WHERE org_id = 2)
ORDER BY id
