-- A1: a full sort on a nullable key, no bound at all.
-- expect: has(Sort)
SELECT id, amount FROM sales ORDER BY amount DESC, id
