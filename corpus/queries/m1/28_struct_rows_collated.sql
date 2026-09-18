-- expect: not(Sort)
SELECT l_orderkey, l_linenumber FROM lineitem ORDER BY l_orderkey, l_linenumber
