-- D207 (part 2b): a suffixed name that already exists among the output names is refused at
-- prepare naming the column and the suffix -- a consumer looks the sibling up by name, and a
-- silent rename would be a silent failure.
SELECT first_name, 'x' AS first_name__disclosure FROM members
