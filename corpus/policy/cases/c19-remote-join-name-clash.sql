-- CALCITE-597, CALCITE-5402, CALCITE-7642, CALCITE-7663: a pushed join of two tables that share
-- a column name. The generated text must not expose two identically named output columns, and
-- must not resolve one side's column to the other's.
SELECT m.id, o.id, o.status
FROM members m JOIN orders o ON o.member_id = m.id
