-- §5 corpus 08. SQLite takes no NULLS FIRST/LAST clause and puts NULLs first ascending, so a sort
-- that wants them last is not one SQLite would deliver: it stays local (D89). The DuckDB twin of
-- the same query pushes it, which is corpus 03.
-- expect: has(RemoteQuery)
-- expect: has(TopN)
SELECT l_orderkey, l_comment
FROM sqlite.lineitem
ORDER BY l_comment
LIMIT 5
