-- D316: ILIKE is never pushed. DuckDB folds the Turkish capital İ that Chalk's LOWER leaves alone,
-- and PostgreSQL folds by its database's locale, so ILIKE is a filter above the remote query; the
-- rest of the statement still travels.
-- expect: has(RemoteQuery)
-- expect: has(Filter)
-- expect: has_function(ILIKE)
SELECT c_name
FROM duck.customer
WHERE c_name ILIKE 'CUSTOMER#00001%'
ORDER BY c_name
