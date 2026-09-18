-- D156, LOCAL: no predicate of this table is pushed, so the tenant set never appears in remote
-- query text. The rows are the same and `row_predicate_pushed` is false, honestly.
SELECT * FROM members ORDER BY id
