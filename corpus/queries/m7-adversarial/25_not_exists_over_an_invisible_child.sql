-- §3.7. The negative form, which is the one that reads a table's *absences*: a member with no
-- orders. Orders the principal cannot see are not there to be found, so the answer is about their
-- own rows and never about the rows the row predicate removed.
-- expect: principals(all)
SELECT m.id FROM members m
WHERE NOT EXISTS (SELECT 1 FROM orders o WHERE o.member_id = m.id)
ORDER BY m.id
