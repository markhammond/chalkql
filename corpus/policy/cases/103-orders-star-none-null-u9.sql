-- §8 corpus 18 under the default policy: the same column is a typed NULL and the output type is
-- widened to nullable, because a value that looks like data is not an acknowledgement.
SELECT * FROM orders ORDER BY id
