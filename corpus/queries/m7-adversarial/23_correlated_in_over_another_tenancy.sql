-- §3.8. A sub-query is another occurrence of the same table and is rewritten as one, so the inner
-- read is the principal's own rows in the other organisation rather than that organisation's rows.
-- The membership test then compares two disclosed values.
-- expect: principals(all)
SELECT id FROM members
WHERE first_name IN (SELECT m2.first_name FROM members m2 WHERE m2.org_id = 1)
ORDER BY id
