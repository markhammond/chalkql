-- §8 corpus 04, as u2: every visible row is masked, so the LIKE runs against initials only.
SELECT * FROM members WHERE first_name LIKE 'T%'
