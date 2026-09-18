-- CALCITE-7453, CALCITE-4923, ADR 0025 V65: one star plus one named column, under
-- UndisclosedColumns = Omit and an entitlement whose default disclosure is NONE. Omit edits
-- RelRoot.fields, so the index list the report and the root projection share is rewritten here;
-- the column dropped must be `postcode` and the join's field offsets must survive the edit.
SELECT m.*, o.status
FROM members m JOIN orders o ON o.member_id = m.id
