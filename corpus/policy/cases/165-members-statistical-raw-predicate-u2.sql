-- D203 (part 2b): the raw predicate the opt-in exists for, in an aggregate-only statement. The
-- group has two rows and k is two, so it survives.
SELECT COUNT(*) AS n FROM members WHERE first_name = 'Rina'
