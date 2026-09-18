-- §3.8, §3.7. "Does this member have any order at all?" — an existence question that puts no value
-- in the output and reads no column of the child but its correlation key. Orders the principal
-- cannot see are not there to be found, so the answer is about their own rows. The correlation is
-- on one column because an INNER correlate on two is a shape this planner does not decorrelate
-- (`14-windows-ii.md` §8).
-- expect: principals(all)
SELECT m.id FROM members m
WHERE EXISTS (SELECT 1 FROM orders o WHERE o.member_id = m.id)
ORDER BY m.id
