-- §3.1. A set operation is a second way to compare a column with a list, and `UNION` is the one
-- that hides which side a row came from. What is unioned is the disclosed value.
-- expect: principals(all)
SELECT n FROM (
  SELECT first_name AS n FROM members
  UNION
  SELECT CAST(g AS VARCHAR) AS n FROM (VALUES ('T'), ('B')) AS v(g)) u
ORDER BY n
