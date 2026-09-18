-- D207 (part 2b): the sibling of an undisclosed column says Undisclosed, which is what keeps an
-- empty value from passing as data.
SELECT postcode FROM members ORDER BY id
