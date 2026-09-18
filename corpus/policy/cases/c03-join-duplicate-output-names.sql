-- CALCITE-5316, CALCITE-597, CALCITE-5402, CALCITE-7663, CALCITE-6157: two output columns that
-- would both be called `id`. rootProject uniquifies them the way Calcite does (id, id0); the
-- generated SQL for a pushed subtree has the same problem and no such step.
SELECT m.id, o.id
FROM members m JOIN orders o ON o.member_id = m.id
