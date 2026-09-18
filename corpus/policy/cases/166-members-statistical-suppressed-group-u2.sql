-- D203 (part 2b): the same shape over a group of one is suppressed -- the group is dropped, not
-- NULLed, so the statement returns no row at all.
SELECT COUNT(*) AS n FROM members WHERE first_name = 'Petra'
