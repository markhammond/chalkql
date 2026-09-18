-- §5 corpus 01. One RemoteQuery; the generated SQL is a golden; RowsFetched counts the matching
-- rows only, because the filter ran in DuckDB.
-- expect: has(RemoteQuery)
-- expect: not(Filter)
SELECT l_orderkey, l_quantity
FROM duck.lineitem
WHERE l_shipdate >= DATE '1995-01-01' AND l_discount BETWEEN 0.05 AND 0.07
