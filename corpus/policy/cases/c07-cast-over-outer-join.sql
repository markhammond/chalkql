-- CALCITE-7488, CALCITE-7489, CALCITE-4399: a CAST in a projection over a LEFT JOIN, which is
-- what PROJECT_JOIN_TRANSPOSE pushes and where a nullability-narrowing cast produces a row-type
-- mismatch. The cast column is `note`, whose disclosure is decided per row for this principal.
SELECT m.id, CAST(o.note AS VARCHAR) AS note_text, m.first_name
FROM members m LEFT JOIN orders o ON o.member_id = m.id AND o.status = 'DONE'
