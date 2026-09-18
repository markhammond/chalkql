-- §8 corpus 22 (part 2b): the filtered count over the same k-anonymous keys.
SELECT first_name, COUNT(*) AS n, COUNT(*) FILTER (WHERE first_name LIKE 'R%') AS r
FROM members GROUP BY first_name ORDER BY first_name
