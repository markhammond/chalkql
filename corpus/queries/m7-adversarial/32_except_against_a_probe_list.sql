-- §3.1. The complement of 31, which is what an attacker uses when the hits are the common case: the
-- values that are *not* in the dictionary.
-- expect: principals(all)
SELECT n FROM (
  SELECT first_name AS n FROM members
  EXCEPT
  SELECT CAST(g AS VARCHAR) AS n FROM (VALUES ('T'), ('B')) AS v(g)) u
ORDER BY n
