-- §3.1. A predicate the optimiser puts *above* an aggregate rather than below it, in case the
-- rewrite only reaches the ones below. `HAVING` reads the group key, which is the disclosed value.
-- expect: principals(all)
SELECT first_name, COUNT(*) AS n
FROM members GROUP BY first_name HAVING first_name > 'B' ORDER BY first_name
