-- §5 corpus 11. An IN list of 20 members, well inside the in-box source's 1 000-member ceiling.
-- The ceiling itself is PushdownRulesTest's, where it can be varied.
-- expect: has(RemoteQuery)
-- expect: not(Filter)
SELECT l_orderkey, l_partkey
FROM duck.lineitem
WHERE l_partkey IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20)
ORDER BY l_orderkey, l_partkey
