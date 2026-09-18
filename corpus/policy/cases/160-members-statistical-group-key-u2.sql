-- §8 corpus 22, D203 (part 2b): with `statistical` on the column, a raw value may be a grouping
-- key in a statement whose every output column is an aggregate or a group key, and the groups
-- below k are dropped rather than NULLed.
SELECT first_name, COUNT(*) AS n FROM members GROUP BY first_name ORDER BY first_name
