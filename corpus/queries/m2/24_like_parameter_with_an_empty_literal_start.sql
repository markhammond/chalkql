-- D314: bound to _nt%, whose literal start is empty, a parameter pattern on a prefix index asks the
-- trie for the empty prefix — every name — and the lookup's residual keeps the four whose second and
-- third characters are nt: Int, Int32, Int64, Interesting.
-- expect: has(IndexLookup)
-- expect: index(ix_terms_name)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=all
SELECT name, text FROM terms WHERE name LIKE ?
