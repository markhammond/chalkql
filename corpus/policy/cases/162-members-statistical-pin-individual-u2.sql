-- D203 (part 2b): a predicate on a declared unique key pins an individual, so it is refused in a
-- statistical statement whatever the group sizes are.
SELECT first_name, COUNT(*) AS n FROM members WHERE id = 2 GROUP BY first_name
