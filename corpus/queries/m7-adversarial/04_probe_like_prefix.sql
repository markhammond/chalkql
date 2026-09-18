-- §3.1. A prefix search walks a value one character at a time. Against an initial mask the first
-- character is all there is, so the walk stops where the mask does.
-- expect: principals(all)
SELECT id FROM members WHERE last_name LIKE 'N%' ORDER BY id
