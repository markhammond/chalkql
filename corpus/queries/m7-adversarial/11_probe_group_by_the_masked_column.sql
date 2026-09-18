-- §3.1. Grouping partitions the rows by a value, so the group keys *are* the distinct values. The
-- keys are the disclosed values, which is what makes grouping ordinary rather than an escape.
-- expect: principals(all)
SELECT first_name, COUNT(*) AS n FROM members GROUP BY first_name ORDER BY first_name
