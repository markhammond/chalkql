-- The target joined to the endpoint of its own path. The statement's join is the host's and has
-- nothing to do with the chain the policy builds; what it shows is that the supplier a row is
-- confined by and the supplier the statement reads are the same one.
-- expect: principals(all)
SELECT p.id, r.name FROM positions p JOIN products r ON r.id = p.product_id ORDER BY p.id
