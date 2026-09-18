-- §3.8. A dictionary: a list of guesses the attacker brings, joined to the protected column, so one
-- statement tests every candidate at once. The join compares the disclosed value, so a guess
-- matches exactly when the mask does.
-- expect: principals(all)
SELECT m.id, g.guess
FROM members m JOIN (VALUES ('T'), ('B'), ('A'), ('K'), ('L')) AS g(guess)
  ON m.first_name = g.guess
ORDER BY m.id, g.guess
