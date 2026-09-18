-- §5 corpus 13. The remote scan is pushed and the client-bodied filter stays above it: a client body
-- is never pushed, because the implementation is in the host process.
-- expect: has(RemoteQuery)
-- expect: has(Filter)
-- expect: has_user_function(main.bucket_price)
SELECT c_custkey, c_acctbal
FROM duck.customer
WHERE bucket_price(CAST(c_acctbal AS DOUBLE), 500.0) > 2000.0
ORDER BY c_custkey
