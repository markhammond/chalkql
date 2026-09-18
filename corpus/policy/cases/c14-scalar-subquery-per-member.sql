-- CALCITE-7405, CALCITE-7574, CALCITE-7430, CALCITE-3978, CALCITE-4318, CALCITE-4984: a
-- correlated scalar sub-query in the select list. Chalk keeps it as a RexSubQuery (expand=false),
-- removes it with PROJECT_SUB_QUERY_TO_CORRELATE and then decorrelates -- three passes that
-- renumber fields, on the shape all six issues are reported against.
SELECT m.id, m.first_name,
       (SELECT COUNT(*) FROM orders o WHERE o.member_id = m.id) AS n
FROM members m
