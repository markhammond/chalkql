-- §3.1. The suffix half of the same walk, which a prefix mask cannot answer at all: the disclosed
-- value is one character, so a suffix of two never matches however the raw value ends.
-- expect: principals(all)
SELECT id FROM members WHERE last_name LIKE '%g' ORDER BY id
