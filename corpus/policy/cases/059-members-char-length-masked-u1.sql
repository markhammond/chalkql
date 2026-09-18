-- §3.1: any scalar function over a sanitised value is ordinary data; the length of the disclosed
-- value is 1 exactly where the initial mask applied.
SELECT id FROM members WHERE CHAR_LENGTH(first_name) = 1 ORDER BY id
