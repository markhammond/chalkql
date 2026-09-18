-- §3.1. Every value at once, concatenated, which is the shape that would turn one row of output
-- into the whole column. It concatenates the disclosed values.
-- expect: principals(all)
-- libraries: POSTGRESQL
SELECT STRING_AGG(first_name, ',' ORDER BY first_name) AS names FROM members
