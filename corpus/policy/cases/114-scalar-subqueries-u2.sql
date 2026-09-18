-- Two scalar sub-queries, each over its own entitled leaf, each with its own Filter_R.
SELECT (SELECT COUNT(*) FROM orders) AS n, (SELECT COUNT(*) FROM members) AS m
