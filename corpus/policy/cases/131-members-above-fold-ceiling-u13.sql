-- §2 item 2: a list of more than `fold_max_rows` rows is not made literal; it becomes a
-- semi-join against the ContextTable, and the rows of the list are not in the plan.
SELECT id FROM members ORDER BY id
