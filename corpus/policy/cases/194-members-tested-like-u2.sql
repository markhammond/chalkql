-- D261 §3: a shape the rule does not name reads the placeholder, exactly as under NONE.
SELECT id FROM members WHERE national_id LIKE 'NID%' ORDER BY id
