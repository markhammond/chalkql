-- The same statement under a global grant, where the value is raw: the one NULL note is found.
SELECT id FROM orders WHERE note IS NULL ORDER BY id
