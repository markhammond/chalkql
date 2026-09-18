-- §8 corpus 19: an equality join on tokens. The fingerprint mask does what it exists for -- two
-- rows with the same name match without either name being disclosed.
SELECT m.id FROM members m JOIN members m2 ON m.first_name = m2.first_name ORDER BY m.id
