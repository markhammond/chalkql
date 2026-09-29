-- D313: the three-operand LIKE reaches a prefix index through its escape. The ESCAPE names a
-- character the pattern never uses, which is how a statement written for arbitrary input usually
-- reads, and the pattern under it is still the bare prefix Int: the range carries the escape so the
-- client reads the pattern as the statement wrote it, and nothing is left to re-check.
-- expect: has(IndexLookup)
-- expect: index(ix_terms_name)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT name, text FROM terms WHERE name LIKE 'Int%' ESCAPE '!'
