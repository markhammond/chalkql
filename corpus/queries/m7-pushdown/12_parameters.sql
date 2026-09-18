-- §5 corpus 12. A dynamic parameter becomes a placeholder in the generated SQL and is bound per
-- execution; the source that does not take parameters keeps the filter local, which
-- GeneratedSqlTest checks where the descriptor can be varied.
-- expect: has(RemoteQuery)
-- expect: not(Filter)
SELECT l_orderkey, l_shipdate
FROM duck.lineitem
WHERE l_shipdate >= @from AND l_orderkey < 300
ORDER BY l_orderkey
