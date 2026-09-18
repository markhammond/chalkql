-- §8 corpus 12, last clause: a bare reference under a numeric CAST is the one permitted wrapping
-- -- it is the shape people write, and a cast cannot select a row.
SELECT AVG(CAST(amount AS DOUBLE)) AS a FROM orders
