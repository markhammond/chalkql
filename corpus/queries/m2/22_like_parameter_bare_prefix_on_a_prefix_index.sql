-- D314: a parameter pattern is a prefix lookup whose LIKE the lookup itself re-checks, because the
-- range covers only the bound value's literal start. Bound to a bare prefix — Int% — the range is the
-- whole answer: the lookup says so at bind, and every row it reads is a row it produces.
-- expect: has(IndexLookup)
-- expect: index(ix_terms_name)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT name, text FROM terms WHERE name LIKE ?
