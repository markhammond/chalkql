-- §8 corpus 23, D208: `otherwise: FULL` on a table that carries a row predicate. The statement
-- is never planned: registration refuses the catalog naming the table and the column.
SELECT postcode FROM members
