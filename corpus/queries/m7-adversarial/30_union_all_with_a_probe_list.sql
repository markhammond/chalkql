-- §3.1. The bag form, which keeps duplicates: how many times a guess appears in the result is how
-- many rows carry it, which is a count the DISTINCT form hides.
-- expect: principals(all)
SELECT n FROM (
  SELECT first_name AS n FROM members
  UNION ALL
  SELECT CAST(g AS VARCHAR) AS n FROM (VALUES ('T'), ('B')) AS v(g)) u
ORDER BY n
