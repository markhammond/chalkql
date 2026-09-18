-- D266. The role condition of a column rule takes the same conjoined term as the row predicate, so
-- a verdict under a confined grant is decided on the same row the predicate admits.
SELECT id, note FROM orders ORDER BY id
