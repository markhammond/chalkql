-- The negative twin of 19: reading one `customer` column is what keeps the join, because nothing
-- else can produce it.
-- expect: has(HashJoin)
SELECT o.o_orderkey, c.c_name
FROM orders o LEFT JOIN customer c ON o.o_custkey = c.c_custkey
WHERE o.o_orderkey < 200
ORDER BY o.o_orderkey
