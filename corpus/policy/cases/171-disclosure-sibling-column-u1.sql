-- D207 (part 2b), IncludeDisclosureColumns: a sibling STRING column holds the disclosure name
-- per row, so a grid can tell a mask from a value without asking twice.
SELECT first_name FROM members ORDER BY id
