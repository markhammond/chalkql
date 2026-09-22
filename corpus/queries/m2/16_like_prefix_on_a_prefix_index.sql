-- D282: the same pattern against a trie. `terms`'s index is a prefix index — it answers prefixes
-- and claims no ordering — so the client sends it the prefix itself rather than a range, and the
-- structure walks straight to it. The rows come back in whatever order the trie walks them, which is
-- what INDEX_KIND_PREFIX promises and no more.
-- expect: has(IndexLookup)
-- expect: index(ix_terms_name)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT name, text FROM terms WHERE name LIKE 'Int%'
