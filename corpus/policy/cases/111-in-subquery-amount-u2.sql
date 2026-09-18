-- An IN sub-query over a column that is FULL for this principal: an ordinary predicate.
SELECT id FROM members WHERE id IN (SELECT member_id FROM orders WHERE amount > 100)
ORDER BY id
