-- §3.1. The sharpest of the four: an intersection returns exactly the guesses that were right, one
-- row per hit and nothing else. They are right about the mask.
-- expect: principals(all)
SELECT n FROM (
  SELECT first_name AS n FROM members
  INTERSECT
  SELECT CAST(g AS VARCHAR) AS n FROM (VALUES ('T'), ('B'), ('Z')) AS v(g)) u
ORDER BY n
