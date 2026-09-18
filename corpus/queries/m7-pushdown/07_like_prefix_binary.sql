-- §5 corpus 07. A LIKE prefix under a binary collation is pushed. The case-insensitive twin is
-- PushdownRulesTest's, where the profile can be varied without a second database.
-- expect: has(RemoteQuery)
-- expect: not(Filter)
SELECT c_name
FROM duck.customer
WHERE c_name LIKE 'Customer#00001%'
ORDER BY c_name
