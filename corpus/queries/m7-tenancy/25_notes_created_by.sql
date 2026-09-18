-- §8 corpus 25. The created-by fail-safe: a principal who holds no grant at all still sees the rows
-- they created, and what they see of them is the CreatorSees option.
-- expect: principals(all)
SELECT * FROM notes ORDER BY id
