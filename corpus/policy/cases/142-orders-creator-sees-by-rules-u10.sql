-- D206, CreatorSees = ByRules: the same rows, and the tenancy rules unchanged -- with no grant
-- the creator sees that the row exists and nothing else.
SELECT id, note, amount FROM orders ORDER BY id
