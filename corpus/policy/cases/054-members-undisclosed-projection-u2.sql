-- D159, D161: an undeclared column under `default_disclosure = NONE`, named explicitly, is a
-- placeholder and is reported Undisclosed -- never omitted, never an error.
SELECT postcode FROM members ORDER BY id
