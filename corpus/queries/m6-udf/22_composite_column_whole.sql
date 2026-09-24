-- A composite column (D302): a member of an in-process table whose type is a record reads as a
-- COMPOSITE of the record's properties, named as declared. Selected whole, each reaches the host as
-- one Arrow struct column; `ask` and `venue` are NULL where the quote has none.
-- expect: has(Read)
-- expect: count(FieldAccess)=0
SELECT id, bid, ask, venue
FROM quotes
ORDER BY id
