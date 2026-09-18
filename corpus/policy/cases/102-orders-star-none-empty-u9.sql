-- §8 corpus 18: an undeclared NOT NULL DATE under PlaceholdersAsEmpty is the epoch, and stays
-- NOT NULL as declared.
SELECT * FROM orders ORDER BY id
