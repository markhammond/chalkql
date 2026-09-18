-- D261 class 6, and §36.3's reason for the rule: a comparison with a *column* is refused precisely
-- so that a list of candidate values cannot turn one probe into a thousand. The other operand here
-- is a column of the same row, so the comparison is over the placeholder.
-- expect: principals(all)
-- expect: policy(u9)
SELECT id FROM members WHERE national_id = postcode ORDER BY id
