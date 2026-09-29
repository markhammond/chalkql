-- D316: ILIKE folds case as LOWER does, so no index answers it — not the prefix index on name, which
-- answers the case-sensitive LIKE 'Int%' (18), and not an ordered one. It is a filter over the scan,
-- and it keeps Int, Int32, Int64 and Interesting whatever case the pattern is written in. Before
-- D316 it was planned as LIKE and kept none of them (F154).
-- expect: has(Filter)
-- expect: not(IndexLookup)
-- expect: has_function(ILIKE)
-- expect: not_function(LIKE)
SELECT name, text FROM terms WHERE name ILIKE 'INT%'
