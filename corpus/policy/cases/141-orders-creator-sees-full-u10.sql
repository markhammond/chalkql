-- D206, CreatorSees = Full: the rows the principal created are visible whatever their tenancy,
-- and their protected columns are disclosed, on the grounds that the principal wrote them.
SELECT id, note, amount FROM orders ORDER BY id
