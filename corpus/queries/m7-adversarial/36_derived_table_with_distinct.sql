-- §3.1. `DISTINCT` over a derived table: the set of values the column takes, with the row count
-- thrown away. It is the set of *disclosed* values.
-- expect: principals(all)
SELECT DISTINCT first_name FROM (SELECT first_name FROM members) x ORDER BY first_name
