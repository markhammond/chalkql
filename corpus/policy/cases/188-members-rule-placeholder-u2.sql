-- D224: the rule that withholds the value is the one that says what stands in its place. An agent
-- gets the agent rule's own stand-in on a STRING column and on a DATE column, and both are reported
-- REDACTED — a placeholder is never data.
SELECT id, first_name, dob FROM members ORDER BY id
