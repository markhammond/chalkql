-- D314: the same statement bound to Int_4, which is not a prefix. The range is its literal start,
-- Int — four names — and the lookup's residual keeps the one the whole pattern matches, Int64. A
-- plan made for a prefix used to refuse this value; any value is now answered.
-- expect: has(IndexLookup)
-- expect: index(ix_terms_name)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned_at_most=4
SELECT name, text FROM terms WHERE name LIKE ?
