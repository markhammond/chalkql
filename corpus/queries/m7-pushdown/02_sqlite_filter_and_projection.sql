-- §5 corpus 02. The same query on SQLite, so the two dialect goldens can be read side by side.
-- expect: has(RemoteQuery)
-- expect: not(Filter)
SELECT l_orderkey, l_quantity
FROM sqlite.lineitem
WHERE l_shipdate >= DATE '1995-01-01' AND l_discount BETWEEN 0.05 AND 0.07
