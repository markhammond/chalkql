-- §8 corpus 05: the disclosed value is the NULL mask, so every visible row matches; there is no
-- query-field rule to remove the row from the statement.
SELECT * FROM members WHERE national_id IS NULL
