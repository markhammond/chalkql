-- The same statement WITHOUT the opt-in: not an error, and not a raw predicate either -- the
-- comparison is against the mask (§3.1), which no five-letter literal can equal.
SELECT COUNT(*) AS n FROM members WHERE first_name = 'Rina'
