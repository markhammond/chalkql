-- §3.8: the same statement for an agent becomes UPPER over the initial and stays local; no
-- five-character value can equal a one-character mask, so the answer is empty.
SELECT id FROM members WHERE UPPER(first_name) = 'TOMAS' ORDER BY id
