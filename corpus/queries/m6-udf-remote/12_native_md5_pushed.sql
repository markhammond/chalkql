-- §5 corpus 12. A native function is spelled the way its source spells it and is pushed into that
-- source; the generated SQL is a golden.
-- expect: has(RemoteQuery)
SELECT c_custkey, duck.md5(c_name) AS h
FROM duck.customer
ORDER BY c_custkey
